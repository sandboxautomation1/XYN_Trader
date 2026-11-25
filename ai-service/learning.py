"""Persistent MT5 candle intake and supervised retraining for reviewed candidates."""

from __future__ import annotations

import hashlib
import json
import math
import os
import sqlite3
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import pandas as pd

from generate_candidates import generate_candidates
from prepare_dataset import prepare_dataset

ROOT = Path(__file__).resolve().parent
DATABASE_PATH = Path(os.environ.get("XYN_LEARNING_DB", str(ROOT / "data" / "learning.sqlite3")))
CANDIDATE_DIRECTORY = Path(
    os.environ.get("XYN_LEARNING_MODEL_DIR", str(ROOT / "models" / "candidates"))
)
MAXIMUM_CANDLES = max(1000, int(os.environ.get("XYN_LEARNING_MAX_CANDLES", "5000")))
HORIZONS = ("1h", "24h", "48h")
MINIMUM_LABELS = 30
_cycle_lock = threading.Lock()


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _connect() -> sqlite3.Connection:
    DATABASE_PATH.parent.mkdir(parents=True, exist_ok=True)
    connection = sqlite3.connect(DATABASE_PATH, timeout=30)
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA journal_mode=WAL")
    connection.execute(
        """
        CREATE TABLE IF NOT EXISTS candles (
            symbol TEXT NOT NULL,
            timestamp_utc TEXT NOT NULL,
            open REAL NOT NULL,
            high REAL NOT NULL,
            low REAL NOT NULL,
            close REAL NOT NULL,
            PRIMARY KEY (symbol, timestamp_utc)
        )
        """
    )
    connection.execute(
        "CREATE TABLE IF NOT EXISTS learning_state (key TEXT PRIMARY KEY, value TEXT NOT NULL)"
    )
    connection.commit()
    return connection


def _load_state(connection: sqlite3.Connection) -> dict[str, Any]:
    row = connection.execute(
        "SELECT value FROM learning_state WHERE key = 'status'"
    ).fetchone()
    if row is None:
        return {
            "status": "waiting_for_data",
            "symbol": "XAUUSD",
            "candle_interval": "1h",
            "last_ingest_utc": None,
            "last_cycle_utc": None,
            "last_error": None,
            "active_models": [],
            "approval_required": True,
            "horizons": {horizon: _empty_horizon() for horizon in HORIZONS},
        }
    try:
        state = json.loads(row["value"])
    except (TypeError, json.JSONDecodeError):
        state = {}
    state.setdefault("horizons", {})
    for horizon in HORIZONS:
        state["horizons"][horizon] = {
            **_empty_horizon(),
            **state["horizons"].get(horizon, {}),
        }
    return state


def _empty_horizon() -> dict[str, Any]:
    return {
        "training_state": "waiting_for_data",
        "candidate_rows": 0,
        "matured_labels": 0,
        "positive_labels": 0,
        "minimum_labels": MINIMUM_LABELS,
        "dataset_sha256": None,
        "candidate_artifact": None,
        "model_version": None,
        "holdout_auc": None,
        "calibration_method": "none",
        "is_calibrated": False,
        "calibration_rows": 0,
        "raw_holdout_brier_score": None,
        "holdout_brier_score": None,
        "raw_holdout_expected_calibration_error": None,
        "holdout_expected_calibration_error": None,
        "baseline_brier_score": None,
        "beats_prevalence_baseline_brier": None,
        "activation_eligible": False,
        "activation_reason": "Кандидатът още не е оценен.",
        "active_model_version": None,
        "message": "Изчакват се реални MT5 данни.",
    }


def _save_state(connection: sqlite3.Connection, state: dict[str, Any]) -> None:
    connection.execute(
        "INSERT INTO learning_state(key, value) VALUES('status', ?) "
        "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
        (json.dumps(state, ensure_ascii=False),),
    )
    connection.commit()


def ingest_candles(candles: list[dict[str, Any]]) -> dict[str, Any]:
    """Upsert only completed H1 bars, retaining a bounded rolling history."""
    now = pd.Timestamp.now(tz="UTC")
    rows: list[tuple[str, str, float, float, float, float]] = []
    skipped_open = 0
    for candle in candles:
        timestamp = pd.Timestamp(candle["timestamp_utc"])
        if timestamp.tzinfo is None:
            raise ValueError("Candle timestamp must include a timezone.")
        timestamp = timestamp.tz_convert("UTC")
        if timestamp + pd.Timedelta(hours=1) > now:
            skipped_open += 1
            continue
        rows.append(
            (
                "XAUUSD",
                timestamp.isoformat(),
                float(candle["open"]),
                float(candle["high"]),
                float(candle["low"]),
                float(candle["close"]),
            )
        )

    connection = _connect()
    try:
        before = connection.total_changes
        connection.executemany(
            """
            INSERT INTO candles(symbol, timestamp_utc, open, high, low, close)
            VALUES(?, ?, ?, ?, ?, ?)
            ON CONFLICT(symbol, timestamp_utc) DO UPDATE SET
                open=excluded.open, high=excluded.high, low=excluded.low, close=excluded.close
            WHERE candles.open != excluded.open OR candles.high != excluded.high
               OR candles.low != excluded.low OR candles.close != excluded.close
            """,
            rows,
        )
        changed = connection.total_changes - before
        connection.execute(
            "DELETE FROM candles WHERE symbol = 'XAUUSD' AND timestamp_utc < "
            "(SELECT timestamp_utc FROM candles WHERE symbol = 'XAUUSD' "
            "ORDER BY timestamp_utc DESC LIMIT 1 OFFSET ?)",
            (MAXIMUM_CANDLES - 1,),
        )
        state = _load_state(connection)
        state["last_ingest_utc"] = _utc_now()
        state["last_ingested_candles"] = len(rows)
        state["last_changed_candles"] = changed
        state["last_skipped_open_candles"] = skipped_open
        state["status"] = "collecting" if changed else state.get("status", "collecting")
        _save_state(connection, state)
        total = connection.execute(
            "SELECT COUNT(*) AS count FROM candles WHERE symbol = 'XAUUSD'"
        ).fetchone()["count"]
        return {
            "accepted_candles": len(rows),
            "changed_candles": changed,
            "skipped_open_candles": skipped_open,
            "stored_candles": total,
            "training_scheduled": changed > 0,
        }
    finally:
        connection.close()


def get_status() -> dict[str, Any]:
    connection: sqlite3.Connection | None = None
    try:
        connection = _connect()
        state = _load_state(connection)
        state["stored_candles"] = connection.execute(
            "SELECT COUNT(*) AS count FROM candles WHERE symbol = 'XAUUSD'"
        ).fetchone()["count"]
        state["maximum_stored_candles"] = MAXIMUM_CANDLES
        state["active_models"] = _active_model_horizons()
        return _attach_candidate_review(state)
    finally:
        if connection is not None:
            connection.close()


def _active_model_horizons() -> list[str]:
    model_directory = Path(
        os.environ.get("XYN_MODEL_PATH", os.environ.get("XYN_MODEL_DIR", str(ROOT / "models")))
    )
    if model_directory.is_file():
        return ["custom"]
    return [
        horizon
        for horizon in HORIZONS
        if (model_directory / f"lightgbm_{horizon}.joblib").is_file()
    ]


def _model_directory() -> Path:
    return Path(
        os.environ.get("XYN_MODEL_PATH", os.environ.get("XYN_MODEL_DIR", str(ROOT / "models")))
    ).expanduser().resolve()


def _active_model_versions(model_directory: Path) -> dict[str, str]:
    versions: dict[str, str] = {}
    for horizon in HORIZONS:
        artifact = model_directory / f"lightgbm_{horizon}.joblib"
        if not artifact.is_file():
            continue
        promotion_path = artifact.with_suffix(".promotion.json")
        try:
            promotion = json.loads(promotion_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        model_version = promotion.get("model_version")
        if isinstance(model_version, str) and model_version:
            versions[horizon] = model_version
    return versions


def _candidate_reviews(state: dict[str, Any]) -> tuple[list[dict[str, Any]], dict[str, str]]:
    """Rank all persisted candidate reports against the same gate used by promotion."""
    from app import MODEL_SCHEMA_VERSION, POLICY_PATH

    candidate_root = CANDIDATE_DIRECTORY.expanduser().resolve()
    model_directory = _model_directory()
    model_path_is_file = model_directory.is_file()
    active_versions = _active_model_versions(model_directory)
    reviews: list[dict[str, Any]] = []
    reasons: dict[str, str] = {}
    latest_versions = {
        horizon: state.get("horizons", {}).get(horizon, {}).get("model_version")
        for horizon in HORIZONS
    }
    seen_latest_versions: set[str] = set()
    try:
        active_policy = json.loads(POLICY_PATH.read_text(encoding="utf-8"))
        active_policy_version = str(active_policy["policy_version"])
        active_policy_sha256 = hashlib.sha256(POLICY_PATH.read_bytes()).hexdigest()
    except (KeyError, OSError, json.JSONDecodeError):
        return reviews, {horizon: "Активната версия на правилата не може да бъде прочетена." for horizon in HORIZONS}

    for horizon in HORIZONS:
        if model_path_is_file:
            reasons[horizon] = "Активният модел е конфигуриран като единичен файл; промотирането изисква директория."
    if model_path_is_file:
        return reviews, reasons

    if candidate_root.is_dir():
        report_paths = sorted(candidate_root.glob("*.joblib.report.json"))
    else:
        report_paths = []

    for report_path in report_paths:
        if not report_path.resolve().is_relative_to(candidate_root):
            continue
        artifact_path = report_path.with_name(report_path.name[: -len(".report.json")]).resolve()
        if not artifact_path.is_relative_to(candidate_root):
            continue
        if not artifact_path.is_file():
            continue

        try:
            report = json.loads(report_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue

        if not isinstance(report, dict):
            continue
        horizon = report.get("horizon")
        if horizon not in HORIZONS:
            continue
        model_version = report.get("model_version")
        if not isinstance(model_version, str) or not model_version.strip():
            continue
        if model_version == latest_versions.get(horizon):
            seen_latest_versions.add(model_version)
        if report.get("schema_version") != MODEL_SCHEMA_VERSION:
            if model_version == latest_versions.get(horizon):
                reasons[horizon] = "Кандидатът използва несъвместима схема на признаците."
            continue
        if (
            report.get("policy_version") != active_policy_version
            or report.get("policy_sha256") != active_policy_sha256
        ):
            if model_version == latest_versions.get(horizon):
                reasons[horizon] = "Кандидатът е обучен с друга версия на правилата."
            continue

        try:
            auc = float(report["holdout_auc"])
            brier = float(report["holdout_brier_score"])
            baseline = float(report["baseline_brier_score"])
        except (KeyError, TypeError, ValueError):
            if model_version == latest_versions.get(horizon):
                reasons[horizon] = "Отчетът няма валидни holdout AUC и Brier метрики."
            continue
        if (
            not all(math.isfinite(value) for value in (auc, brier, baseline))
            or not 0 <= auc <= 1
            or not 0 <= brier <= 1
            or not 0 < baseline <= 1
        ):
            if model_version == latest_versions.get(horizon):
                reasons[horizon] = "Метриките на кандидата са невалидни."
            continue

        if auc < 0.5:
            if model_version == latest_versions.get(horizon):
                reasons[horizon] = f"Holdout AUC {auc:.3f} е под минималните 0.500."
            continue
        if brier >= baseline:
            if model_version == latest_versions.get(horizon):
                reasons[horizon] = f"Brier {brier:.3f} не е по-добър от базовия {baseline:.3f}."
            continue
        if active_versions.get(horizon) == model_version:
            if model_version == latest_versions.get(horizon):
                reasons[horizon] = "Този кандидат вече е активен."
            continue

        skill = 1.0 - brier / baseline
        horizon_state = state.get("horizons", {}).get(horizon, {})
        try:
            dataset_rows = max(0, int(report.get("dataset_rows", 0) or 0))
            training_positive_rate = float(report.get("training_positive_rate", 0) or 0)
        except (TypeError, ValueError):
            dataset_rows = 0
            training_positive_rate = 0.0
        if not math.isfinite(training_positive_rate):
            training_positive_rate = 0.0
        reviews.append(
            {
                "horizon": horizon,
                "model_version": model_version,
                "candidate_path": str(artifact_path),
                "holdout_auc": auc,
                "holdout_brier_score": brier,
                "baseline_brier_score": baseline,
                "brier_skill_score": skill,
                "calibration_method": str(report.get("calibration_method", "none")),
                "is_calibrated": bool(report.get("is_calibrated", False)),
                "matured_labels": int(
                    horizon_state.get("matured_labels", dataset_rows)
                    if model_version == latest_versions.get(horizon)
                    else dataset_rows
                ),
                "positive_labels": int(
                    horizon_state.get(
                        "positive_labels",
                        round(dataset_rows * training_positive_rate),
                    )
                    if model_version == latest_versions.get(horizon)
                    else round(dataset_rows * training_positive_rate)
                ),
            }
        )
        if model_version == latest_versions.get(horizon):
            reasons[horizon] = f"Покрива праговете; Brier Skill е {skill:.3f}."

    for horizon in HORIZONS:
        latest_version = latest_versions.get(horizon)
        if latest_version and latest_version not in seen_latest_versions:
            reasons[horizon] = "Отчетът или файлът на последния обучен кандидат липсва."
        elif not latest_version:
            reasons[horizon] = "Няма последен обучен кандидат за този хоризонт."

    reviews.sort(
        key=lambda item: (
            item["brier_skill_score"],
            item["holdout_auc"],
            item["matured_labels"],
        ),
        reverse=True,
    )
    return reviews, reasons


def _public_candidate(candidate: dict[str, Any]) -> dict[str, Any]:
    return {key: value for key, value in candidate.items() if key != "candidate_path"}


def _attach_candidate_review(state: dict[str, Any]) -> dict[str, Any]:
    eligible, reasons = _candidate_reviews(state)
    active_versions = _active_model_versions(_model_directory())
    for horizon in HORIZONS:
        horizon_state = state["horizons"].setdefault(horizon, _empty_horizon())
        latest_version = horizon_state.get("model_version")
        horizon_state["activation_eligible"] = any(
            item["horizon"] == horizon and item["model_version"] == latest_version
            for item in eligible
        )
        horizon_state["activation_reason"] = reasons.get(horizon, "Няма наличен отчет за кандидата.")
        horizon_state["active_model_version"] = active_versions.get(horizon)
    state["recommended_candidate"] = _public_candidate(eligible[0]) if eligible else None
    return state


def activate_best_candidate(approved_by: str) -> dict[str, Any]:
    """Promote the best currently eligible candidate after an explicit UI approval."""
    approver = approved_by.strip()
    if len(approver) < 2 or len(approver) > 120 or any(ord(character) < 32 for character in approver):
        raise ValueError("Въведи име или номер на преглед с дължина от 2 до 120 знака.")
    if not _cycle_lock.acquire(blocking=False):
        raise RuntimeError("Обучението се изпълнява в момента. Опитай отново след края на цикъла.")

    connection: sqlite3.Connection | None = None
    try:
        connection = _connect()
        state = _load_state(connection)
        candidates, _ = _candidate_reviews(state)
        if not candidates:
            raise ValueError("Няма кандидат, който да покрива праговете за активиране.")

        best = candidates[0]
        candidate_path = Path(best["candidate_path"])
        destination = _model_directory() / f"lightgbm_{best['horizon']}.joblib"
        from promote_model import promote

        promotion = promote(
            candidate_path=candidate_path,
            destination=destination,
            approved_by=approver,
            minimum_auc=0.5,
            maximum_brier=float(best["baseline_brier_score"]),
            replace_existing=destination.exists(),
        )
        state["last_activation_utc"] = promotion["promoted_at_utc"]
        state["last_activated_candidate"] = {
            "horizon": promotion["horizon"],
            "model_version": promotion["model_version"],
            "approved_by": promotion["approved_by"],
        }
        _save_state(connection, state)
        return promotion
    finally:
        if connection is not None:
            connection.close()
        _cycle_lock.release()


def run_cycle() -> dict[str, Any]:
    """Generate historical scenarios, label matured outcomes, and train candidates."""
    if not _cycle_lock.acquire(blocking=False):
        return {"status": "already_running"}

    try:
        connection = _connect()
        try:
            state = _load_state(connection)
            count = connection.execute(
                "SELECT COUNT(*) AS count FROM candles WHERE symbol = 'XAUUSD'"
            ).fetchone()["count"]
            state["last_cycle_utc"] = _utc_now()
            state["last_error"] = None
            if count < 100:
                state["status"] = "collecting"
                state["last_error"] = "Нужни са поне 100 затворени H1 свещи за изчисленията."
                _save_state(connection, state)
                return state

            data_directory = DATABASE_PATH.parent
            candle_path = data_directory / "candles.csv"
            candidates_path = data_directory / "candidates.csv"
            candles_frame = pd.read_sql_query(
                "SELECT timestamp_utc, open, high, low, close FROM candles "
                "WHERE symbol = 'XAUUSD' ORDER BY timestamp_utc",
                connection,
            )
            candles_frame.to_csv(candle_path, index=False)

            try:
                generation = generate_candidates(
                    candle_path,
                    candidates_path,
                    Path(os.environ.get("XYN_SIGNAL_POLICY_PATH", str(ROOT / "signal_policy.json"))),
                    "1h",
                )
            except Exception as error:  # Keep intake alive and expose the cycle error in status.
                state["status"] = "cycle_error"
                state["last_error"] = f"Генерирането на исторически сценарии се провали ({type(error).__name__}): {error}"
                _save_state(connection, state)
                return state

            for horizon in HORIZONS:
                horizon_state = state["horizons"].get(horizon, _empty_horizon())
                dataset_path = data_directory / "datasets" / f"{horizon}.csv"
                try:
                    prepared = prepare_dataset(
                        candle_path, candidates_path, dataset_path, horizon, "1h"
                    )
                    horizon_state.update(
                        candidate_rows=prepared["candidate_rows"],
                        matured_labels=prepared["labeled_rows"],
                        positive_labels=prepared["positive_rows"],
                        minimum_labels=MINIMUM_LABELS,
                    )
                    if prepared["labeled_rows"] < MINIMUM_LABELS:
                        horizon_state["training_state"] = "collecting_labels"
                        horizon_state["message"] = (
                            f"Има {prepared['labeled_rows']} узрели резултата; нужни са поне {MINIMUM_LABELS}."
                        )
                        state["horizons"][horizon] = horizon_state
                        continue

                    positive_rows = int(prepared["positive_rows"])
                    negative_rows = int(prepared["labeled_rows"]) - positive_rows
                    if positive_rows == 0 or negative_rows == 0:
                        horizon_state["training_state"] = "not_trainable_yet"
                        class_guidance = (
                            "Активната стратегия е настроена за 48h; за обучение на 1h са нужни "
                            "по-фини свещи или коригирано етикетиране."
                            if horizon == "1h"
                            else "Добави наблюдавани резултати и от двата класа преди повторно обучение."
                        )
                        horizon_state["message"] = (
                            f"Наборът {horizon} има само един клас: {positive_rows} положителни и "
                            f"{negative_rows} отрицателни резултата. За бинарно обучение са нужни и двата класа; "
                            f"{class_guidance}"
                        )
                        state["horizons"][horizon] = horizon_state
                        continue

                    digest = hashlib.sha256(dataset_path.read_bytes()).hexdigest()
                    if digest == horizon_state.get("dataset_sha256") and horizon_state.get("candidate_artifact"):
                        horizon_state["training_state"] = "candidate_ready"
                        horizon_state["message"] = "Няма нови узрели резултати; последният кандидат е запазен за преглед."
                        state["horizons"][horizon] = horizon_state
                        continue

                    CANDIDATE_DIRECTORY.mkdir(parents=True, exist_ok=True)
                    artifact_path = CANDIDATE_DIRECTORY / f"lightgbm_{horizon}_{digest[:12]}.joblib"
                    from train import train

                    result = train(dataset_path, artifact_path, horizon)
                    report_path = Path(str(result["report"]))
                    report = json.loads(report_path.read_text(encoding="utf-8"))
                    horizon_state.update(
                        training_state="candidate_ready",
                        dataset_sha256=digest,
                        candidate_artifact=str(artifact_path.resolve()),
                        model_version=result["model_version"],
                        holdout_auc=result["holdout_auc"],
                        calibration_method=result["calibration_method"],
                        is_calibrated=result["is_calibrated"],
                        calibration_rows=result["calibration_rows"],
                        raw_holdout_brier_score=result["raw_holdout_brier_score"],
                        holdout_brier_score=result["holdout_brier_score"],
                        raw_holdout_expected_calibration_error=result["raw_holdout_expected_calibration_error"],
                        holdout_expected_calibration_error=result["holdout_expected_calibration_error"],
                        baseline_brier_score=result["baseline_brier_score"],
                        beats_prevalence_baseline_brier=report["beats_prevalence_baseline_brier"],
                        message="Кандидатът е обучен и чака човешки преглед; live моделът не е сменен.",
                    )
                except Exception as error:  # Training requirements can remain unmet while data accumulates.
                    horizon_state["training_state"] = "not_trainable_yet"
                    horizon_state["message"] = f"Обучението още не е възможно: {type(error).__name__}: {error}"
                state["horizons"][horizon] = horizon_state

            ready = [
                horizon
                for horizon in HORIZONS
                if state["horizons"][horizon]["training_state"] == "candidate_ready"
            ]
            state["status"] = "candidate_ready" if ready else "collecting"
            state["candidate_count"] = generation["candidate_count"]
            state["last_cycle_utc"] = _utc_now()
            _save_state(connection, state)
            return state
        finally:
            connection.close()
    finally:
        _cycle_lock.release()
