"""Append-only JSONL audit log for tool executions."""

from __future__ import annotations

import json
import logging
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)


class AuditLog:
    """Persistent append-only audit log that writes events as JSONL files.

    Each day gets its own file: audit-YYYY-MM-DD.jsonl
    """

    def __init__(self, log_dir: Path) -> None:
        self._log_dir = log_dir
        self._log_dir.mkdir(parents=True, exist_ok=True)

    @property
    def log_dir(self) -> Path:
        return self._log_dir

    def _get_log_path(self, date: datetime | None = None) -> Path:
        if date is None:
            date = datetime.now(timezone.utc)
        date_str = date.strftime("%Y-%m-%d")
        return self._log_dir / f"audit-{date_str}.jsonl"

    def log_event(self, event: dict[str, Any]) -> None:
        """Append a single event to today's audit log."""
        if "event_id" not in event:
            event["event_id"] = str(uuid.uuid4())
        if "timestamp" not in event:
            event["timestamp"] = datetime.now(timezone.utc).isoformat()

        log_path = self._get_log_path()
        try:
            with open(log_path, "a", encoding="utf-8") as f:
                f.write(json.dumps(event, default=str) + "\n")
        except Exception:
            logger.exception("Failed to write audit event to %s", log_path)

    def read_events(
        self,
        date: datetime | None = None,
        correlation_id: str | None = None,
        tool_name: str | None = None,
        limit: int = 0,
    ) -> list[dict[str, Any]]:
        """Read events from the audit log with optional filters.

        Args:
            date: Specific date to read from. If None, reads today's log.
            correlation_id: Filter by correlation_id.
            tool_name: Filter by tool_name.
            limit: Maximum number of events to return (0 = unlimited).
        """
        log_path = self._get_log_path(date)
        if not log_path.exists():
            return []

        events: list[dict[str, Any]] = []
        try:
            with open(log_path, "r", encoding="utf-8") as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        event = json.loads(line)
                    except json.JSONDecodeError:
                        continue

                    if correlation_id and event.get("correlation_id") != correlation_id:
                        continue
                    if tool_name and event.get("tool_name") != tool_name:
                        continue

                    events.append(event)
                    if limit > 0 and len(events) >= limit:
                        break
        except Exception:
            logger.exception("Failed to read audit events from %s", log_path)

        return events

    def read_all_events(
        self,
        lookback_days: int = 30,
        tool_name: str | None = None,
        limit: int = 0,
    ) -> list[dict[str, Any]]:
        """Read events from multiple days of audit logs.

        Args:
            lookback_days: Number of days to look back.
            tool_name: Filter by tool_name.
            limit: Maximum number of events to return (0 = unlimited).
        """
        from datetime import timedelta

        events: list[dict[str, Any]] = []
        now = datetime.now(timezone.utc)

        for days_ago in range(lookback_days):
            date = now - timedelta(days=days_ago)
            day_events = self.read_events(
                date=date,
                tool_name=tool_name,
            )
            events.extend(day_events)
            if limit > 0 and len(events) >= limit:
                events = events[:limit]
                break

        return events
