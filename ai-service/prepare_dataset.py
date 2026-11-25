"""Build matured, direction-conditioned training rows from real XAUUSD OHLC candles
and versioned signal candidates produced by the signal/risk engine.

This module deliberately does not invent signal candidates or load demo fixtures.
"""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
import pandas as pd

from app import FEATURE_COLUMNS

HORIZON_HOURS = {"1h": 1, "24h": 24, "48h": 48}
CANDLE_INTERVALS = {
    "1min": pd.Timedelta(minutes=1),
    "5min": pd.Timedelta(minutes=5),
    "15min": pd.Timedelta(minutes=15),
    "30min": pd.Timedelta(minutes=30),
    "1h": pd.Timedelta(hours=1),
    "4h": pd.Timedelta(hours=4),
}
CANDLE_COLUMNS = {"timestamp_utc", "open", "high", "low", "close"}
CANDIDATE_COLUMNS = {
    "signal_id",
    "symbol",
    "timestamp_utc",
    "direction",
    "entry_low",
    "entry_high",
    "stop_loss",
    "tp1",
    "support_distance_atr",
    "resistance_distance_atr",
    "policy_version",
    "policy_sha256",
}


def _read_candles(path: Path) -> pd.DataFrame:
    frame = pd.read_csv(path)
    missing = sorted(CANDLE_COLUMNS - set(frame.columns))
    if missing:
        raise ValueError(f"Candle file is missing required columns: {', '.join(missing)}")
    if "symbol" in frame and not frame["symbol"].eq("XAUUSD").all():
        raise ValueError("The MVP candle file must contain XAUUSD rows only.")

    frame["timestamp_utc"] = pd.to_datetime(frame["timestamp_utc"], utc=True, errors="raise")
    for column in ("open", "high", "low", "close"):
        frame[column] = pd.to_numeric(frame[column], errors="raise")
    if not np.isfinite(frame[["open", "high", "low", "close"]].to_numpy(dtype=float)).all():
        raise ValueError("OHLC values must be finite.")
    if (frame["low"] > frame[["open", "close"]].min(axis=1)).any() or (
        frame["high"] < frame[["open", "close"]].max(axis=1)
    ).any() or (frame["low"] > frame["high"]).any():
        raise ValueError("Candle OHLC values are inconsistent.")
    if frame["timestamp_utc"].duplicated().any():
        raise ValueError("Candle timestamps must be unique.")

    return frame.sort_values("timestamp_utc").reset_index(drop=True)


def _add_features(candles: pd.DataFrame) -> pd.DataFrame:
    result = candles.copy()
    close = result["close"]
    high = result["high"]
    low = result["low"]

    ema20 = close.ewm(span=20, adjust=False, min_periods=20).mean()
    ema50 = close.ewm(span=50, adjust=False, min_periods=50).mean()
    ema12 = close.ewm(span=12, adjust=False, min_periods=12).mean()
    ema26 = close.ewm(span=26, adjust=False, min_periods=26).mean()

    change = close.diff()
    average_gain = change.clip(lower=0).ewm(alpha=1 / 14, adjust=False, min_periods=14).mean()
    average_loss = (-change.clip(upper=0)).ewm(alpha=1 / 14, adjust=False, min_periods=14).mean()
    relative_strength = average_gain / average_loss.replace(0, np.nan)
    rsi = 100 - (100 / (1 + relative_strength))
    rsi = rsi.mask((average_loss == 0) & (average_gain > 0), 100)
    rsi = rsi.mask((average_loss == 0) & (average_gain == 0), 50)

    previous_close = close.shift(1)
    true_range = pd.concat(
        [(high - low), (high - previous_close).abs(), (low - previous_close).abs()], axis=1
    ).max(axis=1)
    atr = true_range.ewm(alpha=1 / 14, adjust=False, min_periods=14).mean()

    upward_move = high.diff()
    downward_move = -low.diff()
    plus_dm = upward_move.where((upward_move > downward_move) & (upward_move > 0), 0.0)
    minus_dm = downward_move.where((downward_move > upward_move) & (downward_move > 0), 0.0)
    smoothed_tr = true_range.ewm(alpha=1 / 14, adjust=False, min_periods=14).mean()
    plus_di = 100 * plus_dm.ewm(alpha=1 / 14, adjust=False, min_periods=14).mean() / smoothed_tr
    minus_di = 100 * minus_dm.ewm(alpha=1 / 14, adjust=False, min_periods=14).mean() / smoothed_tr
    di_sum = (plus_di + minus_di).replace(0, np.nan)
    dx = 100 * (plus_di - minus_di).abs() / di_sum
    adx = dx.ewm(alpha=1 / 14, adjust=False, min_periods=14).mean()

    valid_atr = atr.replace(0, np.nan)
    result["_ema_spread_atr"] = (ema20 - ema50) / valid_atr
    result["_rsi"] = rsi
    result["_macd_atr"] = (ema12 - ema26) / valid_atr
    result["_atr"] = atr
    result["_adx"] = adx
    result["_regime_trending"] = (adx >= 25).astype(float)
    return result


def _direction_code(value: object) -> int:
    normalized = str(value).strip().upper()
    if normalized in {"BUY", "BUY BELOW", "1", "+1"}:
        return 1
    if normalized in {"SELL", "SELL BELOW", "-1"}:
        return -1
    raise ValueError(f"Unsupported scenario direction: {value!r}")


def _features_for(candidate: pd.Series, candle: pd.Series, direction: int) -> dict[str, float]:
    support_distance = float(candidate["support_distance_atr"])
    resistance_distance = float(candidate["resistance_distance_atr"])
    if direction == -1:
        support_distance, resistance_distance = resistance_distance, support_distance
    return {
        "ema_20_50_spread": float(candle["_ema_spread_atr"]) * direction,
        "rsi_14": float(candle["_rsi"] if direction == 1 else 100 - candle["_rsi"]),
        "macd": float(candle["_macd_atr"]) * direction,
        "atr_14": float(candle["_atr"]),
        "adx_14": float(candle["_adx"]),
        "support_distance_atr": support_distance,
        "resistance_distance_atr": resistance_distance,
        "regime_trending": float(candle["_regime_trending"]),
    }


def _validate_candidate(candidate: pd.Series, direction: int) -> None:
    entry_low = float(candidate["entry_low"])
    entry_high = float(candidate["entry_high"])
    stop = float(candidate["stop_loss"])
    target = float(candidate["tp1"])
    support_distance = float(candidate["support_distance_atr"])
    resistance_distance = float(candidate["resistance_distance_atr"])
    if not all(np.isfinite([entry_low, entry_high, stop, target, support_distance, resistance_distance])) or entry_low > entry_high:
        raise ValueError(f"Candidate {candidate['signal_id']} has invalid entry or price levels.")
    if support_distance < 0 or resistance_distance < 0:
        raise ValueError(f"Candidate {candidate['signal_id']} has invalid support/resistance distances.")
    if direction == 1 and not stop < entry_low <= entry_high < target:
        raise ValueError(f"BUY candidate {candidate['signal_id']} must have SL < entry zone < TP1.")
    if direction == -1 and not target < entry_low <= entry_high < stop:
        raise ValueError(f"SELL candidate {candidate['signal_id']} must have TP1 < entry zone < SL.")


def _label_candidate(
    candidate: pd.Series,
    future: pd.DataFrame,
    direction: int,
    horizon_end: pd.Timestamp,
) -> tuple[int, pd.Timestamp] | None:
    """Return target and label end. Same-bar entry/exit ambiguity resolves conservatively."""
    entry_low = float(candidate["entry_low"])
    entry_high = float(candidate["entry_high"])
    stop = float(candidate["stop_loss"])
    target = float(candidate["tp1"])
    entered = False
    for candle in future.loc[future["timestamp_utc"] <= horizon_end].itertuples(index=False):
        if not entered:
            entered = candle.low <= entry_high and candle.high >= entry_low
            # The OHLC bar cannot show whether entry, target, or stop came first.
            # Any same-bar exit is conservatively labeled unsuccessful.
            if entered:
                hit_stop = candle.low <= stop if direction == 1 else candle.high >= stop
                hit_target = candle.high >= target if direction == 1 else candle.low <= target
                if hit_stop or hit_target:
                    return 0, candle.bar_close_utc
            continue

        hit_stop = candle.low <= stop if direction == 1 else candle.high >= stop
        hit_target = candle.high >= target if direction == 1 else candle.low <= target
        if hit_stop and hit_target:
            return 0, candle.bar_close_utc
        if hit_stop:
            return 0, candle.bar_close_utc
        if hit_target:
            return 1, candle.bar_close_utc

    if not entered:
        return None
    if future.empty or future["bar_close_utc"].max() < horizon_end:
        return None
    return 0, horizon_end


def prepare_dataset(
    candles_path: Path,
    candidates_path: Path,
    output_path: Path,
    horizon: str,
    interval: str,
) -> dict[str, object]:
    if horizon not in HORIZON_HOURS:
        raise ValueError(f"Unsupported horizon: {horizon}")
    if interval not in CANDLE_INTERVALS:
        raise ValueError(f"Unsupported candle interval: {interval}")
    candles = _add_features(_read_candles(candles_path))
    candles["bar_close_utc"] = candles["timestamp_utc"] + CANDLE_INTERVALS[interval]
    candidates = pd.read_csv(candidates_path)
    missing = sorted(CANDIDATE_COLUMNS - set(candidates.columns))
    if missing:
        raise ValueError(f"Candidate file is missing required columns: {', '.join(missing)}")
    if candidates["signal_id"].isna().any() or candidates["signal_id"].duplicated().any():
        raise ValueError("Candidate signal_id values must be present and unique.")
    if not candidates["symbol"].eq("XAUUSD").all():
        raise ValueError("The MVP candidate file must contain XAUUSD rows only.")
    if candidates["policy_version"].isna().any() or candidates["policy_sha256"].isna().any():
        raise ValueError("Every candidate must record the signal policy version and checksum.")
    if candidates["policy_version"].nunique() != 1 or candidates["policy_sha256"].nunique() != 1:
        raise ValueError("A training dataset must use exactly one signal policy version and checksum.")
    policy_hash = str(candidates["policy_sha256"].iloc[0])
    if len(policy_hash) != 64 or any(character not in "0123456789abcdef" for character in policy_hash):
        raise ValueError("policy_sha256 must be a lowercase SHA-256 hex digest.")
    candidates["timestamp_utc"] = pd.to_datetime(candidates["timestamp_utc"], utc=True, errors="raise")
    for column in (
        "entry_low",
        "entry_high",
        "stop_loss",
        "tp1",
        "support_distance_atr",
        "resistance_distance_atr",
    ):
        candidates[column] = pd.to_numeric(candidates[column], errors="raise")
    candidates = candidates.sort_values("timestamp_utc").reset_index(drop=True)

    candle_close_times = candles["bar_close_utc"].to_numpy(dtype="datetime64[ns]")
    horizon_delta = pd.Timedelta(hours=HORIZON_HOURS[horizon])
    rows: list[dict[str, object]] = []
    skipped_no_features = 0
    skipped_unmatured = 0
    skipped_untriggered = 0

    for _, candidate in candidates.iterrows():
        direction = _direction_code(candidate["direction"])
        _validate_candidate(candidate, direction)
        timestamp = candidate["timestamp_utc"]
        candle_index = int(
            np.searchsorted(candle_close_times, timestamp.to_datetime64(), side="right") - 1
        )
        if candle_index < 0:
            skipped_no_features += 1
            continue
        feature_candle = candles.iloc[candle_index]
        feature_values = _features_for(candidate, feature_candle, direction)
        if not np.isfinite(np.asarray(list(feature_values.values()), dtype=float)).all():
            skipped_no_features += 1
            continue

        horizon_end = timestamp + horizon_delta
        future = candles.loc[
            (candles["timestamp_utc"] >= timestamp)
            & (candles["bar_close_utc"] <= horizon_end)
        ]
        labeled = _label_candidate(candidate, future, direction, horizon_end)
        if labeled is None:
            if future.empty or future["bar_close_utc"].max() < horizon_end:
                skipped_unmatured += 1
            else:
                skipped_untriggered += 1
            continue

        target_value, label_end = labeled
        row: dict[str, object] = {
            "symbol": "XAUUSD",
            "signal_id": str(candidate["signal_id"]),
            "timestamp_utc": timestamp.isoformat(),
            "label_end_utc": label_end.isoformat(),
            "outcome_matured": True,
            "policy_version": str(candidate["policy_version"]),
            "policy_sha256": str(candidate["policy_sha256"]),
            **feature_values,
            f"target_{horizon}": target_value,
        }
        rows.append(row)

    columns = [
        "symbol",
        "signal_id",
        "timestamp_utc",
        "label_end_utc",
        "outcome_matured",
        "policy_version",
        "policy_sha256",
        *FEATURE_COLUMNS,
        f"target_{horizon}",
    ]
    output = pd.DataFrame(rows, columns=columns)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output.to_csv(output_path, index=False)
    return {
        "horizon": horizon,
        "candle_interval": interval,
        "candidate_rows": len(candidates),
        "labeled_rows": len(output),
        "positive_rows": int(output[f"target_{horizon}"].sum()) if not output.empty else 0,
        "skipped_no_features": skipped_no_features,
        "skipped_unmatured": skipped_unmatured,
        "skipped_untriggered": skipped_untriggered,
        "dataset": str(output_path),
    }


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Prepare matured TP1-before-SL labels from real XAUUSD candles and signal candidates."
    )
    parser.add_argument("--candles", type=Path, required=True, help="Real OHLC candle CSV.")
    parser.add_argument("--candidates", type=Path, required=True, help="Versioned signal candidate CSV.")
    parser.add_argument("--output", type=Path, required=True, help="Prepared dataset CSV.")
    parser.add_argument("--horizon", choices=tuple(HORIZON_HOURS), required=True)
    parser.add_argument("--interval", choices=tuple(CANDLE_INTERVALS), default="1h")
    args = parser.parse_args()
    for key, value in prepare_dataset(
        args.candles, args.candidates, args.output, args.horizon, args.interval
    ).items():
        print(f"{key}: {value}")


if __name__ == "__main__":
    main()
