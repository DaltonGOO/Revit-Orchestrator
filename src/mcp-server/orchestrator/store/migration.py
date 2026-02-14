"""Migrate JSONL audit logs into the SQLite event store."""

from __future__ import annotations

import hashlib
import json
import logging
from pathlib import Path
from typing import Any

from .sqlite_store import SqliteEventStore

logger = logging.getLogger(__name__)


_SYSTEM_EVENT_TYPES = frozenset({
    "workflow_saved",
    "workflow_completed",
    "tool_deleted",
    "tool_updated",
})


class JsonlMigrator:
    """Scans an audit log directory for ``audit-*.jsonl`` files and imports
    their events into a :class:`SqliteEventStore`.

    Idempotent — each file's SHA-256 is recorded in the ``migrations``
    table and skipped on subsequent runs.
    """

    def __init__(self, store: SqliteEventStore, audit_log_dir: Path) -> None:
        self._store = store
        self._dir = audit_log_dir

    def migrate(self) -> int:
        """Import all un-migrated JSONL files.  Returns total events imported."""
        if not self._dir.exists():
            logger.debug("Audit log dir %s does not exist — nothing to migrate", self._dir)
            return 0

        files = sorted(self._dir.glob("audit-*.jsonl"))
        if not files:
            logger.debug("No JSONL audit files found in %s", self._dir)
            return 0

        total = 0
        for path in files:
            count = self._migrate_file(path)
            total += count

        if total > 0:
            logger.info("Migrated %d events from %d JSONL files", total, len(files))
        return total

    def _migrate_file(self, path: Path) -> int:
        """Migrate a single JSONL file.  Returns events imported."""
        file_hash = self._file_hash(path)
        if self._store.is_file_migrated(file_hash):
            logger.debug("Skipping already-migrated file: %s", path.name)
            return 0

        events = self._parse_file(path)
        if not events:
            self._store.record_migration(path.name, file_hash, 0)
            return 0

        # Group by correlation_id to form episodes
        episodes: dict[str, list[dict[str, Any]]] = {}
        standalone: list[dict[str, Any]] = []

        for ev in events:
            cid = ev.get("correlation_id", "")
            if cid:
                episodes.setdefault(cid, []).append(ev)
            else:
                standalone.append(ev)

        count = 0

        # Import grouped episodes
        for cid, ep_events in episodes.items():
            count += self._import_episode(cid, ep_events)

        # Import standalone events — skip system lifecycle events
        # (workflow_saved, workflow_completed, etc.) which are noise
        for ev in standalone:
            eid = ev.get("event_id", "")
            event_type = ev.get("event_type", "")
            if eid and event_type not in _SYSTEM_EVENT_TYPES:
                count += self._import_episode(eid, [ev])

        self._store.record_migration(path.name, file_hash, count)
        logger.info("Migrated %d events from %s", count, path.name)
        return count

    def _import_episode(
        self, episode_id: str, events: list[dict[str, Any]]
    ) -> int:
        """Import a group of events as an episode.  Returns event count."""
        # Sort events by sequence
        events.sort(key=lambda e: e.get("sequence", 0))

        # Determine origin from first event
        origin = events[0].get("origin", "chat") if events else "chat"

        # Start episode
        self._store.start_episode(episode_id=episode_id, origin=origin)

        count = 0
        has_summary = False
        summary_usage: dict[str, Any] = {}

        for ev in events:
            event_type = ev.get("event_type", "")

            if event_type == "correlation_summary":
                has_summary = True
                summary_usage = ev.get("total_usage", {})
                continue

            event_id = ev.get("event_id", "")
            tool_name = ev.get("tool_name", "")
            seq = ev.get("sequence", 0)

            if event_type == "started":
                self._store.log_step(
                    episode_id=episode_id,
                    event_id=event_id,
                    seq=seq,
                    tool_name=tool_name,
                    args=ev.get("args", {}),
                    args_hash=ev.get("args_hash", ""),
                    origin=ev.get("origin", "chat"),
                )
                count += 1

            elif event_type in ("completed", "failed"):
                # Try to update an existing started event
                delta = None
                mcs = ev.get("model_changes_summary")
                if mcs:
                    delta = json.dumps(mcs, default=str)

                error_code = None
                if event_type == "failed":
                    rs = ev.get("result_summary", {})
                    error_code = rs.get("error_code")

                self._store.update_step(
                    event_id=event_id,
                    status=event_type,
                    duration_ms=ev.get("duration_ms", 0),
                    delta_json=delta,
                    error_code=error_code,
                    error_json=json.dumps(ev.get("error")) if ev.get("error") else None,
                )
                # If there was no prior started event, this update is a no-op
                # which is fine — the event simply wasn't imported.

            else:
                # Unknown event type — import as a completed step
                self._store.log_step(
                    episode_id=episode_id,
                    event_id=event_id or ev.get("event_id", ""),
                    seq=seq,
                    tool_name=tool_name or event_type,
                    args=ev,
                    origin=ev.get("origin", "system"),
                )
                # Mark as completed immediately (these are one-shot events)
                self._store.update_step(
                    event_id=event_id or ev.get("event_id", ""),
                    status="completed",
                    duration_ms=ev.get("duration_ms", 0),
                )
                count += 1

        # End episode
        if has_summary or events:
            outcome = "completed"
            # Check if any event failed
            for ev in events:
                if ev.get("event_type") == "failed":
                    outcome = "failed"
                    break
            self._store.end_episode(
                episode_id, outcome=outcome, total_usage=summary_usage
            )

        return count

    @staticmethod
    def _parse_file(path: Path) -> list[dict[str, Any]]:
        """Parse all valid JSON lines from a file."""
        events: list[dict[str, Any]] = []
        try:
            with open(path, "r", encoding="utf-8") as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        events.append(json.loads(line))
                    except json.JSONDecodeError:
                        continue
        except Exception:
            logger.exception("Failed to read JSONL file: %s", path)
        return events

    @staticmethod
    def _file_hash(path: Path) -> str:
        """Compute SHA-256 of a file."""
        h = hashlib.sha256()
        with open(path, "rb") as f:
            for chunk in iter(lambda: f.read(8192), b""):
                h.update(chunk)
        return h.hexdigest()
