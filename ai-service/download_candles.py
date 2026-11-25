"""Download OHLC history from the local MetaTrader 5 bridge into a CSV."""

from __future__ import annotations

import argparse
import json
import os
import time
from datetime import date, datetime, time as datetime_time, timedelta, timezone
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlencode
from urllib.request import Request, urlopen

import pandas as pd

TIMEFRAMES = {
    "1min": "M1",
    "5min": "M5",
    "15min": "M15",
    "30min": "M30",
    "1h": "H1",
    "4h": "H4",
    "1day": "D1",
}
DEFAULT_BRIDGE_URL = "http://127.0.0.1:8000"


def _request_chunk(
    bridge_url: str,
    symbol: str,
    timeframe: str,
    start: date,
    end: date,
) -> list[dict[str, object]]:
    start_utc = datetime.combine(start, datetime_time.min, tzinfo=timezone.utc)
    end_utc = datetime.combine(end, datetime_time.max, tzinfo=timezone.utc).replace(microsecond=0)
    query = urlencode(
        {
            "timeframe": timeframe,
            "start": start_utc.strftime("%Y-%m-%d %H:%M:%S"),
            "end": end_utc.strftime("%Y-%m-%d %H:%M:%S"),
        }
    )
    url = f"{bridge_url.rstrip('/')}/rates_range/{quote(symbol, safe='')}?{query}"
    request = Request(url, headers={"Accept": "application/json"})
    try:
        with urlopen(request, timeout=120) as response:
            payload = json.loads(response.read().decode("utf-8"))
    except HTTPError as error:
        detail = ""
        try:
            body = json.loads(error.read().decode("utf-8"))
            detail = f" {body.get('detail', '')}" if isinstance(body, dict) else ""
        except (UnicodeDecodeError, json.JSONDecodeError):
            pass
        raise RuntimeError(
            f"MT5 bridge returned HTTP {error.code} for {start} through {end}.{detail}"
        ) from None
    except (URLError, TimeoutError):
        raise RuntimeError(
            f"Could not reach MT5 bridge at {bridge_url} for {start} through {end}."
        ) from None
    except json.JSONDecodeError:
        raise RuntimeError("MT5 bridge returned an invalid JSON response.") from None

    if not isinstance(payload, list):
        detail = payload.get("detail", "invalid response") if isinstance(payload, dict) else "invalid response"
        raise RuntimeError(f"MT5 bridge did not return rates for {start} through {end}: {detail}")
    return payload


def download_history(
    bridge_url: str,
    symbol: str,
    interval: str,
    start: date,
    end: date,
    output_path: Path,
    chunk_days: int = 14,
    pause_seconds: float = 0.2,
) -> dict[str, object]:
    if interval not in TIMEFRAMES:
        raise ValueError(f"Unsupported interval: {interval}")
    if start > end:
        raise ValueError("Start date must be on or before end date.")
    if chunk_days < 1 or chunk_days > 90:
        raise ValueError("Chunk size must be between 1 and 90 days.")
    if not symbol.strip():
        raise ValueError("MT5 symbol must not be empty.")

    records: list[dict[str, object]] = []
    chunk_start = start
    request_count = 0
    while chunk_start <= end:
        chunk_end = min(end, chunk_start + timedelta(days=chunk_days - 1))
        rates = _request_chunk(
            bridge_url,
            symbol,
            TIMEFRAMES[interval],
            chunk_start,
            chunk_end,
        )
        for rate in rates:
            try:
                timestamp = int(rate["time"])
                records.append(
                    {
                        "symbol": "XAUUSD",
                        "timestamp_utc": pd.to_datetime(timestamp, unit="s", utc=True),
                        "open": float(rate["open"]),
                        "high": float(rate["high"]),
                        "low": float(rate["low"]),
                        "close": float(rate["close"]),
                        "volume": int(rate.get("tick_volume", 0)),
                    }
                )
            except (KeyError, TypeError, ValueError, OverflowError) as error:
                raise RuntimeError("MT5 bridge returned a candle with invalid fields.") from error

        request_count += 1
        chunk_start = chunk_end + timedelta(days=1)
        if chunk_start <= end and pause_seconds > 0:
            time.sleep(pause_seconds)

    frame = pd.DataFrame(records)
    if frame.empty:
        raise RuntimeError(
            "MT5 returned no candles for the requested range. Load the symbol history in the terminal and check its broker symbol name."
        )
    if not frame["timestamp_utc"].is_monotonic_increasing:
        frame = frame.sort_values("timestamp_utc")
    frame = frame.drop_duplicates("timestamp_utc", keep="last")
    output_path.parent.mkdir(parents=True, exist_ok=True)
    frame.to_csv(output_path, index=False, date_format="%Y-%m-%dT%H:%M:%SZ")
    return {
        "source": "MetaTrader 5 via mt5-bridge",
        "symbol": symbol,
        "timeframe": TIMEFRAMES[interval],
        "requests": request_count,
        "candles": len(frame),
        "output": str(output_path),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Download OHLC history from the MetaTrader 5 bridge.")
    parser.add_argument("--interval", choices=tuple(TIMEFRAMES), required=True)
    parser.add_argument("--start", type=date.fromisoformat, required=True, help="Inclusive UTC date, YYYY-MM-DD.")
    parser.add_argument("--end", type=date.fromisoformat, required=True, help="Inclusive UTC date, YYYY-MM-DD.")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--bridge-url", default=os.environ.get("MT5_BRIDGE_URL", DEFAULT_BRIDGE_URL))
    parser.add_argument("--symbol", default=os.environ.get("MT5_SYMBOL", "XAUUSD"))
    parser.add_argument("--chunk-days", type=int, default=14)
    parser.add_argument("--pause-seconds", type=float, default=0.2)
    args = parser.parse_args()
    result = download_history(
        args.bridge_url,
        args.symbol,
        args.interval,
        args.start,
        args.end,
        args.output,
        args.chunk_days,
        args.pause_seconds,
    )
    for key, value in result.items():
        print(f"{key}: {value}")


if __name__ == "__main__":
    main()
