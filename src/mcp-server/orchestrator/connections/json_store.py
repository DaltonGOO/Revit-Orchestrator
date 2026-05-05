"""JSON file-based persistence for MCP connections."""

from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)


class JsonConnectionStore:
    """Persists MCP connection records to a JSON file.

    Drop-in replacement for the SQLite-based connection persistence that
    ``ConnectionManager`` previously used via ``SqliteEventStore``.
    """

    def __init__(self, path: Path) -> None:
        self._path = path
        self._path.parent.mkdir(parents=True, exist_ok=True)
        self._data: dict[str, dict[str, Any]] = {}
        self._load()

    # ------------------------------------------------------------------
    # Internal helpers
    # ------------------------------------------------------------------

    def _load(self) -> None:
        if self._path.exists():
            try:
                with open(self._path, "r", encoding="utf-8") as f:
                    raw = json.load(f)
                if isinstance(raw, list):
                    # Legacy list format: convert to dict keyed by id
                    self._data = {r["id"]: r for r in raw if "id" in r}
                elif isinstance(raw, dict):
                    self._data = raw
                else:
                    self._data = {}
            except Exception:
                logger.warning("Failed to load connections from %s", self._path, exc_info=True)
                self._data = {}

    def _flush(self) -> None:
        try:
            with open(self._path, "w", encoding="utf-8") as f:
                json.dump(self._data, f, indent=2, default=str)
        except Exception:
            logger.exception("Failed to write connections to %s", self._path)

    # ------------------------------------------------------------------
    # Public API (matches what ConnectionManager expects)
    # ------------------------------------------------------------------

    def load_connections(self) -> list[dict[str, Any]]:
        """Return all stored connections as a list of dicts."""
        return list(self._data.values())

    def save_connection(self, record: dict[str, Any]) -> None:
        """Insert or overwrite a connection record."""
        conn_id = record.get("id", "")
        if not conn_id:
            return
        self._data[conn_id] = record
        self._flush()

    def update_connection(self, conn_id: str, updates: dict[str, Any]) -> None:
        """Merge *updates* into an existing connection record."""
        if conn_id not in self._data:
            return
        self._data[conn_id].update(updates)
        self._flush()

    def delete_connection(self, conn_id: str) -> None:
        """Remove a connection record."""
        if self._data.pop(conn_id, None) is not None:
            self._flush()
