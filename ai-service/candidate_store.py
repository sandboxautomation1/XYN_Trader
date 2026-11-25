"""Persistent archive of generated signal candidates for stale-feed fallback."""

from __future__ import annotations

import json
import os
import sqlite3
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

from storage_paths import learning_database_path

ROOT = Path(__file__).resolve().parent
DATABASE_PATH = learning_database_path(ROOT)
MAXIMUM_ARCHIVED_CANDIDATES = max(100, int(os.environ.get("XYN_MAX_ARCHIVED_CANDIDATES", "2000")))


def _connect() -> sqlite3.Connection:
    DATABASE_PATH.parent.mkdir(parents=True, exist_ok=True)
    connection = sqlite3.connect(DATABASE_PATH, timeout=30)
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA journal_mode=WAL")
    connection.execute(
        """
        CREATE TABLE IF NOT EXISTS candidate_archive (
            signal_id TEXT PRIMARY KEY,
            timestamp_utc TEXT NOT NULL,
            candidate_json TEXT NOT NULL
        )
        """
    )
    connection.execute(
        "CREATE TABLE IF NOT EXISTS candidate_feed_state (id INTEGER PRIMARY KEY CHECK(id = 1), response_json TEXT NOT NULL)"
    )
    connection.commit()
    return connection


def save_candidate_feed(response: dict[str, Any]) -> None:
    """Keep every signal observed across successive live generations, including expired ones."""
    connection = _connect()
    try:
        candidates = response.get("candidates", [])
        connection.execute("BEGIN IMMEDIATE")
        connection.execute(
            "INSERT INTO candidate_feed_state(id, response_json) VALUES(1, ?) "
            "ON CONFLICT(id) DO UPDATE SET response_json = excluded.response_json",
            (json.dumps({key: value for key, value in response.items() if key != "candidates"}),),
        )
        for candidate in candidates:
            connection.execute(
                "INSERT INTO candidate_archive(signal_id, timestamp_utc, candidate_json) VALUES(?, ?, ?) "
                "ON CONFLICT(signal_id) DO UPDATE SET timestamp_utc=excluded.timestamp_utc, "
                "candidate_json=excluded.candidate_json",
                (
                    str(candidate["signalId"]),
                    str(candidate["timestampUtc"]),
                    json.dumps(candidate, ensure_ascii=False),
                ),
            )
        connection.execute(
            "DELETE FROM candidate_archive WHERE signal_id NOT IN "
            "(SELECT signal_id FROM candidate_archive ORDER BY timestamp_utc DESC LIMIT ?)",
            (MAXIMUM_ARCHIVED_CANDIDATES,),
        )
        connection.commit()
    except Exception:
        connection.rollback()
        raise
    finally:
        connection.close()


def get_latest_candidate_feed() -> dict[str, Any] | None:
    connection = _connect()
    try:
        state_row = connection.execute(
            "SELECT response_json FROM candidate_feed_state WHERE id = 1"
        ).fetchone()
        if state_row is None:
            return None

        response = json.loads(state_row["response_json"])
        rows = connection.execute(
            "SELECT candidate_json FROM candidate_archive ORDER BY timestamp_utc DESC"
        ).fetchall()
        candidates = [json.loads(row["candidate_json"]) for row in rows]
        now = datetime.now(timezone.utc)
        for candidate in candidates:
            if candidate.get("status") not in {"CLOSED_TP", "CLOSED_SL", "EXPIRED"}:
                timestamp_text = str(candidate.get("timestampUtc", ""))
                try:
                    timestamp = datetime.fromisoformat(timestamp_text.replace("Z", "+00:00"))
                    horizon = int(candidate["horizonHours"])
                except (ValueError, TypeError, KeyError):
                    continue
                if timestamp.tzinfo is not None and now >= timestamp + timedelta(hours=horizon):
                    candidate["status"] = "EXPIRED"

        response["candidates"] = candidates
        response["isStale"] = True
        response["message"] = (
            "Свежият MT5 feed не е наличен. Показани са запазените сценарии от последното успешно генериране. "
            f"Последно обновяване: {response.get('generatedAtUtc', 'неизвестно')} UTC."
        )
        return response
    finally:
        connection.close()
