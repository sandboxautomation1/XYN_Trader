"""Historical single-position simulator for the versioned XAUUSD signal policy."""

from __future__ import annotations

import sqlite3
import tempfile
import os
from pathlib import Path
from typing import Any

import pandas as pd

from generate_candidates import generate_candidates

ROOT = Path(__file__).resolve().parent
POLICY_PATH = Path(os.environ.get("XYN_SIGNAL_POLICY_PATH", str(ROOT / "signal_policy.json")))
HORIZON_HOURS = {"1h": 1, "24h": 24, "48h": 48}
RISK_FRACTION = 0.01


def _load_candles(database_path: Path) -> pd.DataFrame:
    if not database_path.is_file():
        raise ValueError("No historical candles are stored yet. Start the MT5 collector first.")
    connection = sqlite3.connect(f"{database_path.resolve().as_uri()}?mode=ro", uri=True, timeout=10)
    try:
        frame = pd.read_sql_query(
            "SELECT timestamp_utc, open, high, low, close FROM candles "
            "WHERE symbol = 'XAUUSD' ORDER BY timestamp_utc",
            connection,
        )
    except (sqlite3.Error, pd.errors.DatabaseError) as error:
        raise ValueError("Historical candle storage is unavailable or has no candle table.") from error
    finally:
        connection.close()

    return frame.reset_index(drop=True)


def _utc_bound(value: str | None) -> pd.Timestamp | None:
    if value is None:
        return None
    timestamp = pd.Timestamp(value)
    return timestamp.tz_localize("UTC") if timestamp.tzinfo is None else timestamp.tz_convert("UTC")


def _simulate_candidate(
    candidate: dict[str, Any], candles: pd.DataFrame, horizon_hours: int
) -> dict[str, Any]:
    direction = str(candidate["direction"])
    is_buy = direction == "BUY BELOW"
    decision_time = pd.Timestamp(candidate["timestamp_utc"])
    horizon_end = decision_time + pd.Timedelta(hours=horizon_hours)
    future = candles.loc[
        (candles["timestamp_utc"] >= decision_time)
        & (candles["bar_close_utc"] <= horizon_end)
    ]
    if future.empty or future["bar_close_utc"].max() < horizon_end:
        return {"status": "unmatured"}

    entry = (float(candidate["entry_low"]) + float(candidate["entry_high"])) / 2
    stop = float(candidate["stop_loss"])
    target = float(candidate["tp1"])
    risk = abs(entry - stop)
    if risk <= 0:
        return {"status": "invalid"}

    entered = False
    for candle in future.itertuples(index=False):
        touches_entry = float(candle.low) <= float(candidate["entry_high"]) and float(candle.high) >= float(candidate["entry_low"])
        if not entered:
            if not touches_entry:
                continue
            entered = True
            hit_stop = float(candle.low) <= stop if is_buy else float(candle.high) >= stop
            hit_target = float(candle.high) >= target if is_buy else float(candle.low) <= target
            if hit_stop or hit_target:
                return {
                    "status": "closed_sl",
                    "exit_time_utc": candle.bar_close_utc.isoformat(),
                    "exit_price": stop,
                    "result_r": -1.0,
                }
            continue

        hit_stop = float(candle.low) <= stop if is_buy else float(candle.high) >= stop
        hit_target = float(candle.high) >= target if is_buy else float(candle.low) <= target
        if hit_stop and hit_target:
            return {
                "status": "closed_sl",
                "exit_time_utc": candle.bar_close_utc.isoformat(),
                "exit_price": stop,
                "result_r": -1.0,
            }
        if hit_stop:
            return {
                "status": "closed_sl",
                "exit_time_utc": candle.bar_close_utc.isoformat(),
                "exit_price": stop,
                "result_r": -1.0,
            }
        if hit_target:
            return {
                "status": "closed_tp",
                "exit_time_utc": candle.bar_close_utc.isoformat(),
                "exit_price": target,
                "result_r": abs(target - entry) / risk,
            }

    if not entered:
        return {"status": "untriggered"}

    last = future.iloc[-1]
    exit_price = float(last["close"])
    direction_sign = 1 if is_buy else -1
    return {
        "status": "closed_horizon",
        "exit_time_utc": last["bar_close_utc"].isoformat(),
        "exit_price": exit_price,
        "result_r": direction_sign * (exit_price - entry) / risk,
    }


def run_backtest(
    database_path: Path,
    horizon: str = "48h",
    start: str | None = None,
    end: str | None = None,
) -> dict[str, Any]:
    if horizon not in HORIZON_HOURS:
        raise ValueError("Unsupported horizon; use 1h, 24h, or 48h.")
    candles = _load_candles(database_path)
    if len(candles) < 100:
        raise ValueError(f"At least 100 stored H1 candles are required; found {len(candles)}.")

    candles["timestamp_utc"] = pd.to_datetime(candles["timestamp_utc"], utc=True, errors="raise")
    candles["bar_close_utc"] = candles["timestamp_utc"] + pd.Timedelta(hours=1)
    with tempfile.TemporaryDirectory(prefix="xyn-backtest-") as temporary_directory:
        candle_path = Path(temporary_directory) / "candles.csv"
        candidate_path = Path(temporary_directory) / "candidates.csv"
        candles.loc[:, ["timestamp_utc", "open", "high", "low", "close"]].to_csv(candle_path, index=False)
        generation = generate_candidates(candle_path, candidate_path, POLICY_PATH, "1h")
        candidates = pd.read_csv(candidate_path).to_dict(orient="records")

    start_bound = _utc_bound(start)
    end_bound = _utc_bound(end)
    candidates = [
        candidate
        for candidate in candidates
        if (start_bound is None or pd.Timestamp(candidate["timestamp_utc"]) >= start_bound)
        and (end_bound is None or pd.Timestamp(candidate["timestamp_utc"]) <= end_bound)
    ]
    candidates.sort(key=lambda candidate: candidate["timestamp_utc"])
    closed_trades: list[dict[str, Any]] = []
    untriggered = 0
    unmatured = 0
    skipped_overlap = 0
    busy_until: pd.Timestamp | None = None
    for candidate in candidates:
        entry_time = pd.Timestamp(candidate["timestamp_utc"])
        if busy_until is not None and entry_time < busy_until:
            skipped_overlap += 1
            continue
        result = _simulate_candidate(candidate, candles, HORIZON_HOURS[horizon])
        if result["status"] == "untriggered":
            untriggered += 1
            continue
        if result["status"] == "unmatured":
            unmatured += 1
            continue
        if result["status"] == "invalid":
            continue

        exit_time = pd.Timestamp(result["exit_time_utc"])
        busy_until = exit_time
        closed_trades.append(
            {
                "signal_id": str(candidate["signal_id"]),
                "timestamp_utc": entry_time.isoformat(),
                "exit_time_utc": exit_time.isoformat(),
                "direction": str(candidate["direction"]),
                "status": str(result["status"]),
                "entry_price": (float(candidate["entry_low"]) + float(candidate["entry_high"])) / 2,
                "exit_price": float(result["exit_price"]),
                "result_r": float(result["result_r"]),
            }
        )

    closed_trades.sort(key=lambda trade: trade["exit_time_utc"])
    results = [trade["result_r"] for trade in closed_trades]
    wins = sum(value > 0 for value in results)
    positive_r = sum(value for value in results if value > 0)
    negative_r = abs(sum(value for value in results if value < 0))
    equity = 1.0
    peak = equity
    max_drawdown = 0.0
    for value in results:
        equity *= 1 + RISK_FRACTION * value
        equity = max(equity, 0.0)
        peak = max(peak, equity)
        if peak > 0:
            max_drawdown = max(max_drawdown, (peak - equity) / peak)

    return {
        "symbol": "XAUUSD",
        "timeframe": "H1",
        "horizon": horizon,
        "policy_version": str(generation["policy_version"]),
        "policy_sha256": str(generation["policy_sha256"]),
        "candles": len(candles),
        "from_utc": start_bound.isoformat() if start_bound is not None else candles["timestamp_utc"].min().isoformat(),
        "to_utc": end_bound.isoformat() if end_bound is not None else candles["bar_close_utc"].max().isoformat(),
        "candidate_count": len(candidates),
        "closed_trades": len(closed_trades),
        "untriggered_count": untriggered,
        "unmatured_count": unmatured,
        "skipped_overlap_count": skipped_overlap,
        "win_rate_percent": 100 * wins / len(results) if results else None,
        "net_r": sum(results) if results else 0.0,
        "expectancy_r": sum(results) / len(results) if results else None,
        "profit_factor": positive_r / negative_r if negative_r > 0 else (None if positive_r == 0 else 999.0),
        "max_drawdown_percent": max_drawdown * 100,
        "equity_start": 1.0,
        "equity_end": equity,
        "risk_per_trade_percent": RISK_FRACTION * 100,
        "execution_assumptions": {
            "entry": "Midpoint of the entry zone when an H1 candle touches the zone.",
            "exit": "TP1, stop, or the final H1 close at the selected horizon.",
            "ambiguous_bar": "Stop loss is assumed when a bar touches entry and an exit, or both stop and TP1.",
            "portfolio": "One open position at a time; later signals during a position are skipped.",
            "date_filter": "Start and end filter signal creation times; later stored bars complete each selected outcome horizon.",
            "costs": "Spread, commission, and slippage are excluded.",
        },
        "trades": closed_trades[-100:],
    }
