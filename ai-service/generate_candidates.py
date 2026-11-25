"""Generate versioned swing-zone scenario candidates from real OHLC candles.

The policy is intentionally file-backed so the first strategy can be changed
without changing the training or inference code.
"""

from __future__ import annotations

import argparse
from bisect import bisect_left, bisect_right
import hashlib
import json
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import pandas as pd

from prepare_dataset import CANDLE_INTERVALS, _add_features, _read_candles

POLICY_KEYS = {
    "policy_version",
    "candidate_horizon_hours",
    "swing_lookback_bars",
    "pivot_left_bars",
    "pivot_right_bars",
    "zone_merge_tolerance_atr",
    "zone_half_width_atr",
    "stop_buffer_atr",
    "minimum_entry_gap_atr",
    "maximum_entry_gap_atr",
    "minimum_adx",
    "minimum_tp1_risk_reward",
    "minimum_tp2_risk_reward",
    "minimum_tp3_risk_reward",
    "side_cooldown_hours",
    "minimum_zone_touches",
}


@dataclass(frozen=True)
class PriceZone:
    low: float
    high: float
    touch_count: int


def _load_policy(path: Path) -> dict[str, object]:
    policy = json.loads(path.read_text(encoding="utf-8"))
    missing = sorted(POLICY_KEYS - set(policy))
    extra = sorted(set(policy) - POLICY_KEYS)
    if missing or extra:
        raise ValueError(f"Policy keys mismatch; missing={missing}, unexpected={extra}.")
    if not isinstance(policy["policy_version"], str) or not policy["policy_version"].strip():
        raise ValueError("policy_version must be a non-empty string.")
    positive_keys = (
        "swing_lookback_bars",
        "candidate_horizon_hours",
        "pivot_left_bars",
        "pivot_right_bars",
        "zone_merge_tolerance_atr",
        "zone_half_width_atr",
        "stop_buffer_atr",
        "minimum_entry_gap_atr",
        "maximum_entry_gap_atr",
        "minimum_tp1_risk_reward",
        "minimum_tp2_risk_reward",
        "minimum_tp3_risk_reward",
        "side_cooldown_hours",
        "minimum_zone_touches",
    )
    if any(float(policy[key]) <= 0 for key in positive_keys):
        raise ValueError("All distances, lookbacks, rewards, and cooldown values must be positive.")
    for integer_key in (
        "swing_lookback_bars",
        "candidate_horizon_hours",
        "pivot_left_bars",
        "pivot_right_bars",
        "minimum_zone_touches",
    ):
        if int(policy[integer_key]) != float(policy[integer_key]):
            raise ValueError(f"{integer_key} must be an integer.")
    if float(policy["minimum_adx"]) < 0 or float(policy["minimum_adx"]) > 100:
        raise ValueError("minimum_adx must be between 0 and 100.")
    if float(policy["minimum_entry_gap_atr"]) >= float(policy["maximum_entry_gap_atr"]):
        raise ValueError("maximum_entry_gap_atr must exceed minimum_entry_gap_atr.")
    if not (
        float(policy["minimum_tp1_risk_reward"])
        <= float(policy["minimum_tp2_risk_reward"])
        <= float(policy["minimum_tp3_risk_reward"])
    ):
        raise ValueError("Minimum TP risk/reward thresholds must increase from TP1 to TP3.")
    return policy


def _confirmed_pivots(values: np.ndarray, left: int, right: int, is_high: bool) -> list[int]:
    pivots: list[int] = []
    for index in range(left, len(values) - right):
        before = values[index - left : index]
        after = values[index + 1 : index + right + 1]
        if is_high:
            if values[index] >= before.max() and values[index] > after.max():
                pivots.append(index)
        elif values[index] <= before.min() and values[index] < after.min():
            pivots.append(index)
    return pivots


def _cluster_zones(
    pivots: list[tuple[int, float]],
    pivot_indices: list[int],
    decision_index: int,
    lookback: int,
    right_bars: int,
    atr: float,
    merge_tolerance_atr: float,
    half_width_atr: float,
) -> list[PriceZone]:
    first_index = max(0, decision_index - lookback)
    latest_confirmed = decision_index - right_bars
    lower = bisect_left(pivot_indices, first_index)
    upper = bisect_right(pivot_indices, latest_confirmed)
    levels = sorted(price for _, price in pivots[lower:upper])
    if not levels:
        return []

    groups: list[list[float]] = [[levels[0]]]
    max_cluster_gap = atr * merge_tolerance_atr
    for price in levels[1:]:
        if price - groups[-1][-1] <= max_cluster_gap:
            groups[-1].append(price)
        else:
            groups.append([price])

    zones: list[PriceZone] = []
    for group in groups:
        center = float(np.median(group))
        half_width = atr * half_width_atr
        zones.append(PriceZone(center - half_width, center + half_width, len(group)))
    return zones


def _build_candidate(
    row: pd.Series,
    support_zones: list[PriceZone],
    resistance_zones: list[PriceZone],
    direction: int,
    interval: str,
    policy: dict[str, object],
    policy_sha256: str,
    cooldown_time: pd.Timestamp | None,
) -> dict[str, object] | None:
    price = float(row["close"])
    atr = float(row["_atr"])
    if not np.isfinite([price, atr, float(row["_adx"]) ]).all() or atr <= 0:
        return None
    if float(row["_adx"]) < float(policy["minimum_adx"]):
        return None
    if cooldown_time is not None and row["bar_close_utc"] - cooldown_time < pd.Timedelta(
        hours=float(policy["side_cooldown_hours"])
    ):
        return None

    minimum_gap = atr * float(policy["minimum_entry_gap_atr"])
    maximum_gap = atr * float(policy["maximum_entry_gap_atr"])
    stop_buffer = atr * float(policy["stop_buffer_atr"])

    if direction == 1:
        entries = [zone for zone in support_zones if zone.touch_count >= int(policy["minimum_zone_touches"])]
        entries = [zone for zone in entries if zone.high < price - minimum_gap]
        entries = [zone for zone in entries if price - zone.high <= maximum_gap]
        if not entries:
            return None
        entry_zone = max(entries, key=lambda zone: zone.high)
        stop = entry_zone.low - stop_buffer
        risk = (entry_zone.low + entry_zone.high) / 2 - stop
        targets = sorted(
            (
                zone
                for zone in resistance_zones
                if zone.touch_count >= int(policy["minimum_zone_touches"]) and zone.low > price
            ),
            key=lambda zone: zone.low,
        )
        target_prices = [zone.low for zone in targets[:3]]
        levels_valid = all(
            reward >= risk * float(policy[f"minimum_tp{index}_risk_reward"])
            for index, reward in enumerate((target - (entry_zone.low + entry_zone.high) / 2 for target in target_prices), 1)
        )
        if len(target_prices) < 3 or risk <= 0 or not levels_valid:
            return None
        support_distance = (price - entry_zone.high) / atr
        resistance_distance = (target_prices[0] - price) / atr
        direction_name = "BUY BELOW"
        entry_midpoint = (entry_zone.low + entry_zone.high) / 2
    else:
        entries = [zone for zone in resistance_zones if zone.touch_count >= int(policy["minimum_zone_touches"])]
        entries = [zone for zone in entries if zone.low > price + minimum_gap]
        entries = [zone for zone in entries if zone.low - price <= maximum_gap]
        if not entries:
            return None
        entry_zone = min(entries, key=lambda zone: zone.low)
        stop = entry_zone.high + stop_buffer
        risk = stop - (entry_zone.low + entry_zone.high) / 2
        targets = sorted(
            (
                zone
                for zone in support_zones
                if zone.touch_count >= int(policy["minimum_zone_touches"]) and zone.high < price
            ),
            key=lambda zone: zone.high,
            reverse=True,
        )
        target_prices = [zone.high for zone in targets[:3]]
        levels_valid = all(
            reward >= risk * float(policy[f"minimum_tp{index}_risk_reward"])
            for index, reward in enumerate(((entry_zone.low + entry_zone.high) / 2 - target for target in target_prices), 1)
        )
        if len(target_prices) < 3 or risk <= 0 or not levels_valid:
            return None
        support_distance = (price - target_prices[0]) / atr
        resistance_distance = (entry_zone.low - price) / atr
        direction_name = "SELL BELOW"
        entry_midpoint = (entry_zone.low + entry_zone.high) / 2

    reward_ratios = [abs(target - entry_midpoint) / risk for target in target_prices]
    timeframe = {"1min": "M1", "5min": "M5", "15min": "M15", "30min": "M30", "1h": "H1", "4h": "H4"}[interval]

    return {
        "symbol": "XAUUSD",
        "timeframe": timeframe,
        "timestamp_utc": row["bar_close_utc"].isoformat(),
        "direction": direction_name,
        "entry_order_type": "BUY LIMIT" if direction == 1 else "SELL LIMIT",
        "entry_price": entry_midpoint,
        "entry_low": entry_zone.low,
        "entry_high": entry_zone.high,
        "stop_loss": stop,
        "tp1": target_prices[0],
        "tp2": target_prices[1],
        "tp3": target_prices[2],
        "tp1_risk_reward": reward_ratios[0],
        "tp2_risk_reward": reward_ratios[1],
        "tp3_risk_reward": reward_ratios[2],
        "support_distance_atr": support_distance,
        "resistance_distance_atr": resistance_distance,
        "zone_touch_count": entry_zone.touch_count,
        "adx_14": float(row["_adx"]),
        "atr_14": atr,
        "model_ema_20_50_spread": float(row["_ema20"] - row["_ema50"]),
        "model_rsi_14": float(row["_rsi"]),
        "model_macd": float(row["_macd_atr"] * atr),
        "model_regime_trending": float(row["_regime_trending"]),
        "policy_version": str(policy["policy_version"]),
        "policy_sha256": policy_sha256,
        "_timeframe_code": timeframe,
    }


def generate_candidates(
    candles_path: Path,
    output_path: Path,
    policy_path: Path,
    interval: str,
) -> dict[str, object]:
    if interval not in CANDLE_INTERVALS:
        raise ValueError(f"Unsupported candle interval: {interval}")
    policy = _load_policy(policy_path)
    policy_sha256 = hashlib.sha256(policy_path.read_bytes()).hexdigest()
    candles = _add_features(_read_candles(candles_path))
    candles["bar_close_utc"] = candles["timestamp_utc"] + CANDLE_INTERVALS[interval]
    candles = candles.loc[candles["bar_close_utc"] <= pd.Timestamp.now(tz="UTC")].reset_index(drop=True)
    if candles.empty:
        raise ValueError("No fully closed candles are available for candidate generation.")
    close = candles["close"]
    candles["_ema20"] = close.ewm(span=20, adjust=False, min_periods=20).mean()
    candles["_ema50"] = close.ewm(span=50, adjust=False, min_periods=50).mean()

    left = int(policy["pivot_left_bars"])
    right = int(policy["pivot_right_bars"])
    pivot_high_indices = _confirmed_pivots(candles["high"].to_numpy(dtype=float), left, right, True)
    pivot_low_indices = _confirmed_pivots(candles["low"].to_numpy(dtype=float), left, right, False)
    pivot_highs = [(index, float(candles.iloc[index]["high"])) for index in pivot_high_indices]
    pivot_lows = [(index, float(candles.iloc[index]["low"])) for index in pivot_low_indices]
    pivot_high_positions = [index for index, _ in pivot_highs]
    pivot_low_positions = [index for index, _ in pivot_lows]

    candidates: list[dict[str, object]] = []
    last_candidate_at: dict[int, pd.Timestamp | None] = {1: None, -1: None}
    neutral_bars = 0
    for index, row in candles.iterrows():
        atr = float(row["_atr"]) if pd.notna(row["_atr"]) else np.nan
        if not np.isfinite(atr) or atr <= 0 or pd.isna(row["_adx"]) or pd.isna(row["_ema20"]) or pd.isna(row["_ema50"]):
            neutral_bars += 1
            continue
        support_zones = _cluster_zones(
            pivot_lows,
            pivot_low_positions,
            index,
            int(policy["swing_lookback_bars"]),
            right,
            atr,
            float(policy["zone_merge_tolerance_atr"]),
            float(policy["zone_half_width_atr"]),
        )
        resistance_zones = _cluster_zones(
            pivot_highs,
            pivot_high_positions,
            index,
            int(policy["swing_lookback_bars"]),
            right,
            atr,
            float(policy["zone_merge_tolerance_atr"]),
            float(policy["zone_half_width_atr"]),
        )
        if row["_ema20"] > row["_ema50"]:
            direction = 1
        elif row["_ema20"] < row["_ema50"]:
            direction = -1
        else:
            neutral_bars += 1
            continue

        candidate = _build_candidate(
            row,
            support_zones,
            resistance_zones,
            direction,
            interval,
            policy,
            policy_sha256,
            last_candidate_at[direction],
        )
        if candidate is None:
            neutral_bars += 1
            continue
        candidate["signal_id"] = (
            f"XYN-{candidate['_timeframe_code']}-{row['bar_close_utc'].strftime('%Y%m%dT%H%M%SZ')}-"
            f"{'BUY' if direction == 1 else 'SELL'}"
        )
        candidate.pop("_timeframe_code")
        candidates.append(candidate)
        last_candidate_at[direction] = row["bar_close_utc"]

    columns = [
        "signal_id",
        "symbol",
        "timeframe",
        "timestamp_utc",
        "direction",
        "entry_order_type",
        "entry_price",
        "entry_low",
        "entry_high",
        "stop_loss",
        "tp1",
        "tp2",
        "tp3",
        "tp1_risk_reward",
        "tp2_risk_reward",
        "tp3_risk_reward",
        "support_distance_atr",
        "resistance_distance_atr",
        "zone_touch_count",
        "adx_14",
        "atr_14",
        "model_ema_20_50_spread",
        "model_rsi_14",
        "model_macd",
        "model_regime_trending",
        "policy_version",
        "policy_sha256",
    ]
    output_path.parent.mkdir(parents=True, exist_ok=True)
    pd.DataFrame(candidates, columns=columns).to_csv(output_path, index=False)
    metadata = {
        "policy_version": policy["policy_version"],
        "policy_sha256": policy_sha256,
        "policy": policy,
        "candle_interval": interval,
        "candidate_count": len(candidates),
        "buy_below_count": sum(item["direction"] == "BUY BELOW" for item in candidates),
        "sell_below_count": sum(item["direction"] == "SELL BELOW" for item in candidates),
        "neutral_bars": neutral_bars,
        "candles_sha256": hashlib.sha256(candles_path.read_bytes()).hexdigest(),
        "policy_source": str(policy_path),
        "output": str(output_path),
    }
    metadata_path = output_path.with_suffix(output_path.suffix + ".metadata.json")
    metadata_path.write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    return {key: value for key, value in metadata.items() if key != "policy"}


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate versioned swing-zone signal candidates from real XAUUSD candles.")
    parser.add_argument("--candles", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--policy", type=Path, default=Path("signal_policy.json"))
    parser.add_argument("--interval", choices=tuple(CANDLE_INTERVALS), default="1h")
    args = parser.parse_args()
    result = generate_candidates(args.candles, args.output, args.policy, args.interval)
    for key, value in result.items():
        print(f"{key}: {value}")


if __name__ == "__main__":
    main()
