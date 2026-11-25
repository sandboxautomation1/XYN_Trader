"""Durable local profile and signal journal, separate from market-data retention."""

from __future__ import annotations

import os
import sqlite3
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parent
DATABASE_PATH = Path(
    os.environ.get("XYN_USER_DB", str(ROOT / "data" / "user_data.sqlite3"))
)
LOCAL_PROFILE_ID = "local"
LOCAL_PROFILE_NAME = "Локален профил"


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _connect() -> sqlite3.Connection:
    DATABASE_PATH.parent.mkdir(parents=True, exist_ok=True)
    connection = sqlite3.connect(DATABASE_PATH, timeout=30)
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA journal_mode=WAL")
    connection.execute(
        """
        CREATE TABLE IF NOT EXISTS user_profiles (
            profile_id TEXT PRIMARY KEY,
            display_name TEXT NOT NULL,
            created_at_utc TEXT NOT NULL
        )
        """
    )
    connection.execute(
        """
        CREATE TABLE IF NOT EXISTS user_signal_journal (
            profile_id TEXT NOT NULL REFERENCES user_profiles(profile_id) ON DELETE CASCADE,
            signal_id TEXT NOT NULL,
            is_favorite INTEGER NOT NULL DEFAULT 0 CHECK (is_favorite IN (0, 1)),
            note TEXT NOT NULL DEFAULT '',
            updated_at_utc TEXT NOT NULL,
            PRIMARY KEY (profile_id, signal_id)
        )
        """
    )
    connection.execute(
        "INSERT OR IGNORE INTO user_profiles(profile_id, display_name, created_at_utc) VALUES(?, ?, ?)",
        (LOCAL_PROFILE_ID, LOCAL_PROFILE_NAME, _utc_now()),
    )
    connection.commit()
    return connection


def get_profile() -> dict[str, Any]:
    connection = _connect()
    try:
        profile = connection.execute(
            "SELECT profile_id, display_name, created_at_utc FROM user_profiles WHERE profile_id = ?",
            (LOCAL_PROFILE_ID,),
        ).fetchone()
        entries = connection.execute(
            "SELECT signal_id, is_favorite, note, updated_at_utc FROM user_signal_journal "
            "WHERE profile_id = ? ORDER BY updated_at_utc DESC, signal_id",
            (LOCAL_PROFILE_ID,),
        ).fetchall()
        return {
            "profile_id": profile["profile_id"],
            "display_name": profile["display_name"],
            "created_at_utc": profile["created_at_utc"],
            "signal_journal": [
                {
                    "signal_id": row["signal_id"],
                    "is_favorite": bool(row["is_favorite"]),
                    "note": row["note"],
                    "updated_at_utc": row["updated_at_utc"],
                }
                for row in entries
            ],
        }
    finally:
        connection.close()


def save_signal_entry(signal_id: str, is_favorite: bool, note: str) -> dict[str, Any] | None:
    normalized_note = note.strip()
    connection = _connect()
    try:
        connection.execute("BEGIN IMMEDIATE")
        if not is_favorite and not normalized_note:
            connection.execute(
                "DELETE FROM user_signal_journal WHERE profile_id = ? AND signal_id = ?",
                (LOCAL_PROFILE_ID, signal_id),
            )
            connection.commit()
            return None

        updated_at_utc = _utc_now()
        connection.execute(
            "INSERT INTO user_signal_journal(profile_id, signal_id, is_favorite, note, updated_at_utc) "
            "VALUES(?, ?, ?, ?, ?) "
            "ON CONFLICT(profile_id, signal_id) DO UPDATE SET "
            "is_favorite = excluded.is_favorite, note = excluded.note, updated_at_utc = excluded.updated_at_utc",
            (LOCAL_PROFILE_ID, signal_id, int(is_favorite), normalized_note, updated_at_utc),
        )
        connection.commit()
        return {
            "signal_id": signal_id,
            "is_favorite": is_favorite,
            "note": normalized_note,
            "updated_at_utc": updated_at_utc,
        }
    except Exception:
        connection.rollback()
        raise
    finally:
        connection.close()
