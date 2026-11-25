"""Shared path selection for the learning SQLite store."""

from __future__ import annotations

import os
from pathlib import Path


def learning_database_path(service_root: Path) -> Path:
    """Prefer per-user local storage on Windows; honor explicit overrides."""
    configured_path = os.environ.get("XYN_LEARNING_DB")
    if configured_path:
        return Path(configured_path).expanduser()

    local_app_data = os.environ.get("LOCALAPPDATA")
    if local_app_data:
        return Path(local_app_data) / "XYN_Trader" / "learning.sqlite3"

    return service_root / "data" / "learning.sqlite3"
