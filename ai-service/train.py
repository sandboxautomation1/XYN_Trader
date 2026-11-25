import argparse
import hashlib
import json
import os
from datetime import datetime, timezone
from pathlib import Path

import joblib
import lightgbm as lgb
import numpy as np
import pandas as pd
from sklearn.isotonic import IsotonicRegression
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import brier_score_loss, roc_auc_score
from sklearn.model_selection import TimeSeriesSplit

from app import FEATURE_COLUMNS, MODEL_SCHEMA_VERSION

REQUIRED_COLUMNS = {"symbol", "signal_id", "timestamp_utc", "outcome_matured"}
MINIMUM_ROWS = 30
MINIMUM_CALIBRATION_ROWS = 20
ISOTONIC_MINIMUM_ROWS = 1000


def _calibration_error(labels: np.ndarray, probabilities: np.ndarray, bins: int = 10) -> float | None:
    """Weighted expected calibration error over equal-width probability bins."""
    if len(labels) == 0:
        return None
    edges = np.linspace(0.0, 1.0, bins + 1)
    total = 0.0
    for index in range(bins):
        mask = (probabilities >= edges[index]) & (
            probabilities <= edges[index + 1] if index == bins - 1 else probabilities < edges[index + 1]
        )
        if mask.any():
            total += float(mask.mean()) * abs(float(probabilities[mask].mean()) - float(labels[mask].mean()))
    return total


def _logit(probabilities: np.ndarray) -> np.ndarray:
    clipped = np.clip(probabilities, 1e-6, 1 - 1e-6)
    return np.log(clipped / (1 - clipped)).reshape(-1, 1)


def _fit_calibrator(labels: np.ndarray, probabilities: np.ndarray) -> tuple[object | None, str]:
    if len(labels) < MINIMUM_CALIBRATION_ROWS or np.unique(labels).size != 2:
        return None, "none"
    if len(labels) >= ISOTONIC_MINIMUM_ROWS:
        calibrator = IsotonicRegression(y_min=0.0, y_max=1.0, out_of_bounds="clip")
        calibrator.fit(probabilities, labels)
        return calibrator, "isotonic"
    calibrator = LogisticRegression(C=1.0, solver="lbfgs", random_state=42)
    calibrator.fit(_logit(probabilities), labels)
    return calibrator, "platt_sigmoid"


def _apply_calibrator(calibrator: object | None, method: str, probabilities: np.ndarray) -> np.ndarray:
    if calibrator is None or method == "none":
        return probabilities
    if method == "isotonic":
        return np.asarray(calibrator.predict(probabilities), dtype=float)
    return np.asarray(calibrator.predict_proba(_logit(probabilities))[:, 1], dtype=float)


def _new_classifier() -> lgb.LGBMClassifier:
    return lgb.LGBMClassifier(
        objective="binary",
        n_estimators=100,
        learning_rate=0.05,
        max_depth=3,
        num_leaves=7,
        min_child_samples=5,
        verbosity=-1,
        random_state=42,
    )


def _out_of_fold_probabilities(
    development: pd.DataFrame, target_column: str
) -> tuple[np.ndarray, np.ndarray]:
    labels: list[int] = []
    probabilities: list[float] = []
    splits = min(4, max(2, len(development) // 10))
    for train_indices, calibration_indices in TimeSeriesSplit(n_splits=splits).split(development):
        fold_validation = development.iloc[calibration_indices]
        validation_start = fold_validation.iloc[0]["timestamp_utc"]
        fold_training = development.iloc[train_indices]
        fold_training = fold_training.loc[fold_training["label_end_utc"] < validation_start]
        if len(fold_training) < 2 or fold_training[target_column].nunique() != 2:
            continue
        fold_model = _new_classifier()
        fold_model.fit(fold_training.loc[:, FEATURE_COLUMNS], fold_training[target_column].astype(int))
        fold_probabilities = fold_model.predict_proba(fold_validation.loc[:, FEATURE_COLUMNS])[:, 1]
        labels.extend(fold_validation[target_column].astype(int).tolist())
        probabilities.extend(fold_probabilities.astype(float).tolist())
    return np.asarray(labels, dtype=int), np.asarray(probabilities, dtype=float)


def train(input_path: Path, output_path: Path, horizon: str) -> dict[str, object]:
    frame = pd.read_csv(input_path)
    target_column = f"target_{horizon}"
    required = REQUIRED_COLUMNS | set(FEATURE_COLUMNS) | {target_column}
    missing = sorted(required - set(frame.columns))
    if missing:
        raise ValueError(f"Dataset is missing required columns: {', '.join(missing)}")
    if len(frame) < MINIMUM_ROWS:
        raise ValueError(f"At least {MINIMUM_ROWS} labeled, matured signal outcomes are required.")
    if not frame["symbol"].eq("XAUUSD").all():
        raise ValueError("The MVP training dataset must contain XAUUSD rows only.")
    if frame["signal_id"].isna().any() or frame["timestamp_utc"].isna().any():
        raise ValueError("Every training row must have a signal ID and UTC timestamp.")
    if frame["signal_id"].duplicated().any():
        raise ValueError("Each matured signal outcome must have exactly one training row per horizon.")
    if not frame["outcome_matured"].map(
        lambda value: value is True or str(value).strip().lower() in {"true", "1"}
    ).all():
        raise ValueError("Training rows must be explicitly marked as matured outcomes.")

    policy_version = "legacy-unspecified"
    policy_sha256 = None
    if "policy_version" in frame:
        if frame["policy_version"].isna().any() or frame["policy_version"].nunique() != 1:
            raise ValueError("A model must be trained on exactly one non-empty signal policy version.")
        policy_version = str(frame["policy_version"].iloc[0])
    if "policy_sha256" in frame:
        if frame["policy_sha256"].isna().any() or frame["policy_sha256"].nunique() != 1:
            raise ValueError("A model must be trained on exactly one signal policy checksum.")
        policy_sha256 = str(frame["policy_sha256"].iloc[0])

    frame["timestamp_utc"] = pd.to_datetime(frame["timestamp_utc"], utc=True, errors="raise")
    if "label_end_utc" in frame:
        frame["label_end_utc"] = pd.to_datetime(frame["label_end_utc"], utc=True, errors="raise")
    else:
        # Older prepared files can still be evaluated conservatively by assuming
        # every outcome may consume its full prediction horizon.
        frame["label_end_utc"] = frame["timestamp_utc"] + pd.Timedelta(
            hours={"1h": 1, "24h": 24, "48h": 48}[horizon]
        )
    features = frame.loc[:, FEATURE_COLUMNS].apply(pd.to_numeric, errors="raise")
    if not np.isfinite(features.to_numpy(dtype=float)).all():
        raise ValueError("Feature values must be finite.")

    labels = pd.to_numeric(frame[target_column], errors="raise")
    if not labels.isin([0, 1]).all():
        raise ValueError(f"{target_column} must contain only matured binary outcomes (0 or 1).")
    dataset_class_counts = labels.value_counts().reindex([0, 1], fill_value=0)
    if (dataset_class_counts == 0).any():
        raise ValueError(
            f"{target_column} contains only one outcome class "
            f"(class 0: {int(dataset_class_counts[0])}, class 1: {int(dataset_class_counts[1])}); "
            "binary training requires positive and negative outcomes."
        )

    ordered = frame.assign(**{target_column: labels}).sort_values("timestamp_utc")
    split_index = int(len(ordered) * 0.8)
    holdout = ordered.iloc[split_index:]
    if holdout.empty:
        raise ValueError("A chronological holdout period is required.")
    holdout_start = holdout.iloc[0]["timestamp_utc"]
    development = ordered.iloc[:split_index].loc[
        ordered.iloc[:split_index]["label_end_utc"] < holdout_start
    ]
    if len(development) < 4:
        raise ValueError("Too few development rows remain after purging outcomes overlapping the holdout.")
    training = development
    training_class_counts = training[target_column].value_counts().reindex([0, 1], fill_value=0)
    if (training_class_counts == 0).any():
        raise ValueError(
            "The purged chronological training period must contain both outcome classes "
            f"(class 0: {int(training_class_counts[0])}, class 1: {int(training_class_counts[1])}; "
            f"full dataset class 0: {int(dataset_class_counts[0])}, "
            f"class 1: {int(dataset_class_counts[1])}). Add earlier examples of the missing class "
            "or train with a horizon that has both classes."
        )

    classifier = _new_classifier()
    classifier.fit(training.loc[:, FEATURE_COLUMNS], training[target_column].astype(int))
    holdout_features = holdout.loc[:, FEATURE_COLUMNS]
    holdout_labels = holdout[target_column].astype(int)
    raw_holdout_probabilities = classifier.predict_proba(holdout_features)[:, 1]
    calibration_labels, raw_calibration_probabilities = _out_of_fold_probabilities(development, target_column)
    calibrator, calibration_method = _fit_calibrator(
        calibration_labels, raw_calibration_probabilities
    )
    holdout_probabilities = _apply_calibrator(calibrator, calibration_method, raw_holdout_probabilities)
    training_labels = training[target_column].astype(int)
    raw_holdout_brier = float(brier_score_loss(holdout_labels, raw_holdout_probabilities))
    holdout_brier = float(brier_score_loss(holdout_labels, holdout_probabilities))
    raw_holdout_ece = _calibration_error(holdout_labels.to_numpy(), raw_holdout_probabilities)
    holdout_ece = _calibration_error(holdout_labels.to_numpy(), holdout_probabilities)
    baseline_probability = float(training_labels.mean())
    baseline_probabilities = np.full(len(holdout_labels), baseline_probability)
    baseline_brier = float(brier_score_loss(holdout_labels, baseline_probabilities))
    auc = (
        float(roc_auc_score(holdout_labels, holdout_probabilities))
        if holdout_labels.nunique() == 2
        else None
    )

    dataset_digest = hashlib.sha256(input_path.read_bytes()).hexdigest()
    digest = dataset_digest[:12]
    trained_at = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    artifact = {
        "schema_version": MODEL_SCHEMA_VERSION,
        "feature_columns": list(FEATURE_COLUMNS),
        "horizon": horizon,
        "policy_version": policy_version,
        "policy_sha256": policy_sha256,
        "model_version": f"lgbm-{horizon}-{trained_at}-{digest}",
        "trained_at_utc": datetime.now(timezone.utc).isoformat(),
        "training_rows": len(training),
        "calibration_rows": len(calibration_labels),
        "holdout_rows": len(holdout),
        "training_positive_rate": baseline_probability,
        "holdout_positive_rate": float(holdout_labels.mean()),
        "holdout_auc": auc,
        "calibration_method": calibration_method,
        "is_calibrated": calibrator is not None,
        "raw_holdout_brier_score": raw_holdout_brier,
        "holdout_brier_score": holdout_brier,
        "raw_holdout_expected_calibration_error": raw_holdout_ece,
        "holdout_expected_calibration_error": holdout_ece,
        "baseline_brier_score": baseline_brier,
        "dataset_sha256": dataset_digest,
        "dataset_path": str(input_path),
        "positive_outcome": "TP1 reached before stop loss after entry; ambiguous same-bar outcomes are negative",
        "classifier": classifier,
        "calibrator": calibrator,
    }

    report = {
        "model_version": artifact["model_version"],
        "schema_version": MODEL_SCHEMA_VERSION,
        "horizon": horizon,
        "policy_version": policy_version,
        "policy_sha256": policy_sha256,
        "dataset_sha256": dataset_digest,
        "dataset_rows": len(ordered),
        "training_rows_after_purge": len(training),
        "calibration_rows": len(calibration_labels),
        "holdout_rows": len(holdout),
        "training_positive_rate": baseline_probability,
        "holdout_positive_rate": float(holdout_labels.mean()),
        "holdout_auc": auc,
        "calibration_method": calibration_method,
        "is_calibrated": calibrator is not None,
        "raw_holdout_brier_score": raw_holdout_brier,
        "holdout_brier_score": holdout_brier,
        "raw_holdout_expected_calibration_error": raw_holdout_ece,
        "holdout_expected_calibration_error": holdout_ece,
        "baseline_brier_score": baseline_brier,
        "beats_prevalence_baseline_brier": holdout_brier < baseline_brier,
        "positive_outcome": artifact["positive_outcome"],
        "approval_status": "candidate_requires_review",
        "artifact": str(output_path),
    }

    output_path.parent.mkdir(parents=True, exist_ok=True)
    temporary_path = output_path.with_suffix(output_path.suffix + ".tmp")
    joblib.dump(artifact, temporary_path)
    os.replace(temporary_path, output_path)
    report_path = output_path.with_suffix(output_path.suffix + ".report.json")
    temporary_report = report_path.with_suffix(report_path.suffix + ".tmp")
    temporary_report.write_text(json.dumps(report, indent=2), encoding="utf-8")
    os.replace(temporary_report, report_path)

    return {
        "model_version": artifact["model_version"],
        "training_rows": len(training),
        "calibration_rows": len(calibration_labels),
        "holdout_rows": len(holdout),
        "holdout_auc": auc,
        "calibration_method": calibration_method,
        "is_calibrated": calibrator is not None,
        "raw_holdout_brier_score": raw_holdout_brier,
        "holdout_brier_score": holdout_brier,
        "raw_holdout_expected_calibration_error": raw_holdout_ece,
        "holdout_expected_calibration_error": holdout_ece,
        "baseline_brier_score": baseline_brier,
        "beats_prevalence_baseline_brier": report["beats_prevalence_baseline_brier"],
        "artifact": str(output_path),
        "report": str(report_path),
    }


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Train the XAUUSD LightGBM baseline from matured, outcome-labeled signals."
    )
    parser.add_argument("--input", type=Path, required=True, help="Prepared CSV dataset.")
    parser.add_argument("--output", type=Path, required=True, help="Output joblib artifact path.")
    parser.add_argument("--horizon", choices=("1h", "24h", "48h"), required=True)
    args = parser.parse_args()

    result = train(args.input, args.output, args.horizon)
    for key, value in result.items():
        print(f"{key}: {value}")


if __name__ == "__main__":
    main()
