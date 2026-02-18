"""SQLite-backed episode/event store — duck-type compatible with AuditLog."""

from __future__ import annotations

import json
import logging
import sqlite3
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Optional

from .blob_store import BlobStore

logger = logging.getLogger(__name__)

SCHEMA_VERSION = 2

_SCHEMA_V2_SQL = """\
-- MCP external connections
CREATE TABLE IF NOT EXISTS connections (
    id                    TEXT PRIMARY KEY,
    name                  TEXT NOT NULL UNIQUE,
    transport             TEXT NOT NULL CHECK(transport IN ('stdio','sse','streamable_http')),
    enabled               INTEGER NOT NULL DEFAULT 1,
    command               TEXT NOT NULL DEFAULT '',
    args_json             TEXT NOT NULL DEFAULT '[]',
    env_json              TEXT NOT NULL DEFAULT '{}',
    url                   TEXT NOT NULL DEFAULT '',
    headers_json          TEXT NOT NULL DEFAULT '{}',
    auth_type             TEXT NOT NULL DEFAULT 'none',
    auth_credential_enc   TEXT NOT NULL DEFAULT '',
    server_name           TEXT NOT NULL DEFAULT '',
    server_version        TEXT NOT NULL DEFAULT '',
    discovered_tools_json TEXT NOT NULL DEFAULT '[]',
    created_at            TEXT NOT NULL,
    updated_at            TEXT NOT NULL,
    last_connected_at     TEXT,
    last_error            TEXT,
    status                TEXT NOT NULL DEFAULT 'disconnected'
);
"""

_SCHEMA_SQL = """\
-- Schema versioning
CREATE TABLE IF NOT EXISTS schema_version (
    version  INTEGER PRIMARY KEY,
    applied_at TEXT NOT NULL
);

-- Episodes (one per agentic loop / correlation group)
CREATE TABLE IF NOT EXISTS episodes (
    id               TEXT PRIMARY KEY,
    started_at       TEXT NOT NULL,
    ended_at         TEXT,
    doc_guid         TEXT,
    doc_title        TEXT,
    user_name        TEXT,
    revit_version    TEXT,
    origin           TEXT,
    tool_summary     TEXT,
    tags_json        TEXT,
    outcome          TEXT,
    total_duration_ms INTEGER,
    llm_input_tokens  INTEGER,
    llm_output_tokens INTEGER,
    card_text        TEXT,
    card_features    TEXT,
    embedding        BLOB
);

-- Events (one per tool call within an episode)
CREATE TABLE IF NOT EXISTS events (
    id            TEXT PRIMARY KEY,
    episode_id    TEXT NOT NULL REFERENCES episodes(id),
    seq           INTEGER NOT NULL,
    ts            TEXT NOT NULL,
    tool_name     TEXT NOT NULL,
    action_type   TEXT,
    args_json     TEXT,
    args_hash     TEXT,
    targets_json  TEXT,
    delta_json    TEXT,
    context_json  TEXT,
    pre_hash      TEXT,
    post_hash     TEXT,
    status        TEXT NOT NULL DEFAULT 'started',
    error_code    TEXT,
    error_json    TEXT,
    duration_ms   INTEGER,
    origin        TEXT,
    card_text     TEXT,
    card_features TEXT,
    embedding     BLOB
);
CREATE INDEX IF NOT EXISTS idx_events_episode ON events(episode_id);
CREATE INDEX IF NOT EXISTS idx_events_tool    ON events(tool_name);
CREATE INDEX IF NOT EXISTS idx_events_status  ON events(status);

-- Artifacts (screenshots, overlays, etc.)
CREATE TABLE IF NOT EXISTS artifacts (
    id          TEXT PRIMARY KEY,
    event_id    TEXT NOT NULL REFERENCES events(id),
    kind        TEXT NOT NULL,
    sha256      TEXT NOT NULL,
    path        TEXT NOT NULL,
    size_bytes  INTEGER,
    width       INTEGER,
    height      INTEGER,
    mime_type   TEXT,
    meta_json   TEXT,
    created_at  TEXT NOT NULL,
    UNIQUE(event_id, kind, sha256)
);

-- Entities (cross-project element identity)
CREATE TABLE IF NOT EXISTS entities (
    id                   TEXT PRIMARY KEY,
    doc_guid             TEXT NOT NULL,
    element_unique_id    TEXT NOT NULL,
    category             TEXT,
    family_name          TEXT,
    type_name            TEXT,
    type_fingerprint     TEXT,
    geometry_fingerprint TEXT,
    canonical_ref        TEXT,
    first_seen           TEXT NOT NULL,
    last_seen            TEXT NOT NULL,
    meta_json            TEXT,
    UNIQUE(doc_guid, element_unique_id)
);
CREATE INDEX IF NOT EXISTS idx_entities_canonical ON entities(canonical_ref);

-- Features / embedding cache
CREATE TABLE IF NOT EXISTS features (
    id            TEXT PRIMARY KEY,
    event_id      TEXT NOT NULL REFERENCES events(id),
    feature_json  TEXT NOT NULL,
    embedding     BLOB,
    model_version TEXT,
    created_at    TEXT NOT NULL
);

-- Migration tracking (for JSONL import)
CREATE TABLE IF NOT EXISTS migrations (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    source_file     TEXT NOT NULL,
    source_hash     TEXT NOT NULL,
    events_imported INTEGER NOT NULL DEFAULT 0,
    migrated_at     TEXT NOT NULL
);
"""


class SqliteEventStore:
    """Episode-aware event store backed by SQLite.

    Duck-type compatible with ``AuditLog`` — exposes the same
    ``log_event``, ``read_events``, ``read_all_events`` signatures so it
    can be used as a drop-in replacement.
    """

    def __init__(
        self,
        db_path: Path,
        blob_store: BlobStore | None = None,
    ) -> None:
        self._db_path = db_path
        self._blob_store = blob_store
        db_path.parent.mkdir(parents=True, exist_ok=True)
        self._conn = sqlite3.connect(str(db_path), check_same_thread=False)
        self._conn.row_factory = sqlite3.Row
        self._conn.execute("PRAGMA journal_mode=WAL")
        self._conn.execute("PRAGMA foreign_keys=ON")
        self._ensure_schema()

    @property
    def db_path(self) -> Path:
        return self._db_path

    @property
    def blob_store(self) -> BlobStore | None:
        return self._blob_store

    # ------------------------------------------------------------------
    # Schema management
    # ------------------------------------------------------------------

    def _ensure_schema(self) -> None:
        cur = self._conn.cursor()
        # Check if schema_version table exists
        cur.execute(
            "SELECT name FROM sqlite_master WHERE type='table' AND name='schema_version'"
        )
        if cur.fetchone() is None:
            self._conn.executescript(_SCHEMA_SQL)
            cur.execute(
                "INSERT INTO schema_version (version, applied_at) VALUES (?, ?)",
                (SCHEMA_VERSION, _now_iso()),
            )
            self._conn.commit()
            logger.info("Created event store schema v%d", SCHEMA_VERSION)
        else:
            cur.execute("SELECT MAX(version) FROM schema_version")
            row = cur.fetchone()
            current = row[0] if row else 0
            if current < SCHEMA_VERSION:
                self._migrate_schema(current)

    def _migrate_schema(self, from_version: int) -> None:
        cur = self._conn.cursor()
        if from_version < 2:
            logger.info("Migrating schema from v%d to v2", from_version)
            # Create connections table
            self._conn.executescript(_SCHEMA_V2_SQL)
            # Add traceability columns to events (ignore if they already exist)
            for col in ("connection_id TEXT", "connection_tool TEXT"):
                try:
                    self._conn.execute(f"ALTER TABLE events ADD COLUMN {col}")
                except sqlite3.OperationalError:
                    pass  # column already exists
            cur.execute(
                "INSERT OR REPLACE INTO schema_version (version, applied_at) VALUES (?, ?)",
                (2, _now_iso()),
            )
            self._conn.commit()
            logger.info("Schema migrated to v2")

    # ------------------------------------------------------------------
    # AuditLog duck-type interface
    # ------------------------------------------------------------------

    @property
    def log_dir(self) -> Path:
        """Compatibility shim — returns the parent directory of the DB."""
        return self._db_path.parent

    def log_event(self, event: dict[str, Any]) -> None:
        """Append a single event — AuditLog compatibility.

        Maps old-style flat events into the episodes/events tables.
        """
        event_type = event.get("event_type", "")
        cid = event.get("correlation_id", "")
        tool_name = event.get("tool_name", "")
        event_id = event.get("event_id", str(uuid.uuid4()))

        if event_type == "correlation_summary":
            # End-of-loop summary — update episode
            self._handle_correlation_summary(cid, event)
            return

        if event_type in ("workflow_saved", "tool_deleted", "tool_updated"):
            # Administrative events — store as a standalone event
            self._ensure_episode(cid or event_id, event.get("origin", "system"))
            self._insert_event(
                event_id=event_id,
                episode_id=cid or event_id,
                seq=event.get("sequence", 0),
                tool_name=tool_name or event_type,
                status="completed",
                args_json=json.dumps(event, default=str),
                origin=event.get("origin", "system"),
            )
            return

        if event_type == "started":
            self._ensure_episode(cid, event.get("origin", "chat"))
            self._insert_event(
                event_id=event_id,
                episode_id=cid,
                seq=event.get("sequence", 0),
                tool_name=tool_name,
                status="started",
                args_json=json.dumps(event.get("args", {}), default=str),
                args_hash=event.get("args_hash", ""),
                origin=event.get("origin", "chat"),
            )
        elif event_type in ("completed", "failed"):
            self._update_event_status(
                event_id=event_id,
                status=event_type,
                duration_ms=event.get("duration_ms", 0),
                error_code=event.get("result_summary", {}).get("error_code") if event_type == "failed" else None,
                error_json=json.dumps(event.get("error")) if event.get("error") else None,
                delta_json=json.dumps(event.get("model_changes_summary")) if event.get("model_changes_summary") else None,
            )

    def read_events(
        self,
        date: datetime | None = None,
        correlation_id: str | None = None,
        tool_name: str | None = None,
        limit: int = 0,
    ) -> list[dict[str, Any]]:
        """Read events with optional filters — AuditLog compatibility."""
        clauses: list[str] = []
        params: list[Any] = []

        if correlation_id:
            clauses.append("e.episode_id = ?")
            params.append(correlation_id)
        if tool_name:
            clauses.append("e.tool_name = ?")
            params.append(tool_name)
        if date:
            day_str = date.strftime("%Y-%m-%d")
            clauses.append("e.ts LIKE ?")
            params.append(f"{day_str}%")

        where = (" WHERE " + " AND ".join(clauses)) if clauses else ""
        sql = f"SELECT * FROM events e{where} ORDER BY e.ts"
        if limit > 0:
            sql += f" LIMIT {limit}"

        rows = self._conn.execute(sql, params).fetchall()
        return [self._row_to_audit_event(r) for r in rows]

    def read_all_events(
        self,
        lookback_days: int = 30,
        tool_name: str | None = None,
        limit: int = 0,
    ) -> list[dict[str, Any]]:
        """Read events from multiple days — AuditLog compatibility."""
        clauses: list[str] = []
        params: list[Any] = []

        clauses.append("e.ts >= datetime('now', ?)")
        params.append(f"-{lookback_days} days")

        if tool_name:
            clauses.append("e.tool_name = ?")
            params.append(tool_name)

        where = " WHERE " + " AND ".join(clauses)
        sql = f"SELECT * FROM events e{where} ORDER BY e.ts DESC"
        if limit > 0:
            sql += f" LIMIT {limit}"

        rows = self._conn.execute(sql, params).fetchall()
        return [self._row_to_audit_event(r) for r in rows]

    # ------------------------------------------------------------------
    # Episode API
    # ------------------------------------------------------------------

    def start_episode(
        self,
        episode_id: str | None = None,
        origin: str = "chat",
        context: dict[str, Any] | None = None,
    ) -> str:
        """Create a new episode row.  Returns the episode ID."""
        eid = episode_id or str(uuid.uuid4())
        ctx = context or {}
        self._conn.execute(
            """INSERT OR IGNORE INTO episodes
               (id, started_at, doc_guid, doc_title, user_name,
                revit_version, origin)
               VALUES (?, ?, ?, ?, ?, ?, ?)""",
            (
                eid,
                _now_iso(),
                ctx.get("doc_guid", ""),
                ctx.get("doc_title", ""),
                ctx.get("user_name", ""),
                ctx.get("revit_version", ""),
                origin,
            ),
        )
        self._conn.commit()
        return eid

    def end_episode(
        self,
        episode_id: str,
        outcome: str = "completed",
        total_usage: dict[str, Any] | None = None,
    ) -> None:
        """Finalise an episode with outcome and token usage."""
        usage = total_usage or {}
        # Build tool_summary from events in this episode
        rows = self._conn.execute(
            "SELECT tool_name FROM events WHERE episode_id = ? ORDER BY seq",
            (episode_id,),
        ).fetchall()
        tool_summary = " → ".join(r["tool_name"] for r in rows if r["tool_name"])

        # Compute total duration
        dur_row = self._conn.execute(
            "SELECT SUM(duration_ms) as total FROM events WHERE episode_id = ?",
            (episode_id,),
        ).fetchone()
        total_ms = dur_row["total"] if dur_row and dur_row["total"] else 0

        self._conn.execute(
            """UPDATE episodes SET
                ended_at = ?,
                outcome = ?,
                tool_summary = ?,
                total_duration_ms = ?,
                llm_input_tokens = ?,
                llm_output_tokens = ?
               WHERE id = ?""",
            (
                _now_iso(),
                outcome,
                tool_summary,
                total_ms,
                usage.get("input_tokens", 0),
                usage.get("output_tokens", 0),
                episode_id,
            ),
        )
        self._conn.commit()

    def log_step(
        self,
        episode_id: str,
        event_id: str,
        seq: int,
        tool_name: str,
        args: dict[str, Any],
        args_hash: str = "",
        origin: str = "chat",
    ) -> str:
        """Insert a new event (started) into an episode.  Returns event_id."""
        self._insert_event(
            event_id=event_id,
            episode_id=episode_id,
            seq=seq,
            tool_name=tool_name,
            status="started",
            args_json=json.dumps(args, default=str),
            args_hash=args_hash,
            origin=origin,
        )
        return event_id

    def update_step(
        self,
        event_id: str,
        status: str,
        duration_ms: int = 0,
        delta_json: str | None = None,
        error_code: str | None = None,
        error_json: str | None = None,
        targets_json: str | None = None,
    ) -> None:
        """Update an event with completion / failure data."""
        self._update_event_status(
            event_id=event_id,
            status=status,
            duration_ms=duration_ms,
            error_code=error_code,
            error_json=error_json,
            delta_json=delta_json,
            targets_json=targets_json,
        )

    # ------------------------------------------------------------------
    # Query API
    # ------------------------------------------------------------------

    def get_episode(self, episode_id: str) -> Optional[dict[str, Any]]:
        """Fetch a single episode by ID."""
        row = self._conn.execute(
            "SELECT * FROM episodes WHERE id = ?", (episode_id,)
        ).fetchone()
        return dict(row) if row else None

    def get_episode_events(self, episode_id: str) -> list[dict[str, Any]]:
        """Fetch all events for an episode, ordered by sequence."""
        rows = self._conn.execute(
            "SELECT * FROM events WHERE episode_id = ? ORDER BY seq",
            (episode_id,),
        ).fetchall()
        return [dict(r) for r in rows]

    def query_episodes(
        self,
        doc_guid: str | None = None,
        origin: str | None = None,
        outcome: str | None = None,
        tool_name: str | None = None,
        limit: int = 50,
        offset: int = 0,
    ) -> list[dict[str, Any]]:
        """Query episodes with optional filters."""
        clauses: list[str] = []
        params: list[Any] = []

        if doc_guid:
            clauses.append("ep.doc_guid = ?")
            params.append(doc_guid)
        if origin:
            clauses.append("ep.origin = ?")
            params.append(origin)
        if outcome:
            clauses.append("ep.outcome = ?")
            params.append(outcome)
        if tool_name:
            clauses.append(
                "ep.id IN (SELECT episode_id FROM events WHERE tool_name = ?)"
            )
            params.append(tool_name)

        where = (" WHERE " + " AND ".join(clauses)) if clauses else ""
        sql = f"""SELECT ep.* FROM episodes ep{where}
                  ORDER BY ep.started_at DESC LIMIT ? OFFSET ?"""
        params.extend([limit, offset])

        rows = self._conn.execute(sql, params).fetchall()
        return [dict(r) for r in rows]

    # ------------------------------------------------------------------
    # Artifact API
    # ------------------------------------------------------------------

    def add_artifact(
        self,
        event_id: str,
        kind: str,
        sha256: str,
        path: str,
        size_bytes: int = 0,
        width: int | None = None,
        height: int | None = None,
        mime_type: str = "",
        meta: dict[str, Any] | None = None,
    ) -> str:
        """Insert an artifact row.  Returns the artifact ID."""
        aid = str(uuid.uuid4())
        self._conn.execute(
            """INSERT OR IGNORE INTO artifacts
               (id, event_id, kind, sha256, path, size_bytes,
                width, height, mime_type, meta_json, created_at)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
            (
                aid,
                event_id,
                kind,
                sha256,
                path,
                size_bytes,
                width,
                height,
                mime_type,
                json.dumps(meta) if meta else None,
                _now_iso(),
            ),
        )
        self._conn.commit()
        return aid

    def get_artifacts(self, event_id: str) -> list[dict[str, Any]]:
        """Fetch all artifacts for an event."""
        rows = self._conn.execute(
            "SELECT * FROM artifacts WHERE event_id = ? ORDER BY created_at",
            (event_id,),
        ).fetchall()
        return [dict(r) for r in rows]

    # ------------------------------------------------------------------
    # Migration tracking
    # ------------------------------------------------------------------

    def is_file_migrated(self, source_hash: str) -> bool:
        """Check if a JSONL file has already been migrated."""
        row = self._conn.execute(
            "SELECT 1 FROM migrations WHERE source_hash = ?", (source_hash,)
        ).fetchone()
        return row is not None

    def record_migration(
        self, source_file: str, source_hash: str, events_imported: int
    ) -> None:
        """Record that a JSONL file has been migrated."""
        self._conn.execute(
            """INSERT INTO migrations (source_file, source_hash, events_imported, migrated_at)
               VALUES (?, ?, ?, ?)""",
            (source_file, source_hash, events_imported, _now_iso()),
        )
        self._conn.commit()

    # ------------------------------------------------------------------
    # Internals
    # ------------------------------------------------------------------

    def _ensure_episode(self, episode_id: str, origin: str = "chat") -> None:
        """Create an episode row if it doesn't exist yet."""
        if not episode_id:
            return
        self._conn.execute(
            """INSERT OR IGNORE INTO episodes (id, started_at, origin)
               VALUES (?, ?, ?)""",
            (episode_id, _now_iso(), origin),
        )
        self._conn.commit()

    def _insert_event(
        self,
        event_id: str,
        episode_id: str,
        seq: int,
        tool_name: str,
        status: str,
        args_json: str = "{}",
        args_hash: str = "",
        origin: str = "chat",
        targets_json: str | None = None,
        delta_json: str | None = None,
    ) -> None:
        self._conn.execute(
            """INSERT OR IGNORE INTO events
               (id, episode_id, seq, ts, tool_name, status,
                args_json, args_hash, origin, targets_json, delta_json)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
            (
                event_id,
                episode_id,
                seq,
                _now_iso(),
                tool_name,
                status,
                args_json,
                args_hash,
                origin,
                targets_json,
                delta_json,
            ),
        )
        self._conn.commit()

    def _update_event_status(
        self,
        event_id: str,
        status: str,
        duration_ms: int = 0,
        error_code: str | None = None,
        error_json: str | None = None,
        delta_json: str | None = None,
        targets_json: str | None = None,
    ) -> None:
        self._conn.execute(
            """UPDATE events SET
                status = ?,
                duration_ms = ?,
                error_code = ?,
                error_json = ?,
                delta_json = COALESCE(?, delta_json),
                targets_json = COALESCE(?, targets_json)
               WHERE id = ?""",
            (status, duration_ms, error_code, error_json, delta_json, targets_json, event_id),
        )
        self._conn.commit()

    def _handle_correlation_summary(
        self, correlation_id: str, event: dict[str, Any]
    ) -> None:
        """Handle an end-of-loop summary by finalising the episode."""
        if not correlation_id:
            return
        usage = event.get("total_usage", {})
        self.end_episode(
            correlation_id,
            outcome="completed",
            total_usage=usage,
        )

    @staticmethod
    def _row_to_audit_event(row: sqlite3.Row) -> dict[str, Any]:
        """Convert an events row back to the flat dict format AuditLog consumers expect."""
        r = dict(row)
        event: dict[str, Any] = {
            "event_id": r["id"],
            "timestamp": r["ts"],
            "correlation_id": r["episode_id"],
            "sequence": r["seq"],
            "event_type": r["status"],
            "tool_name": r["tool_name"],
            "args_hash": r.get("args_hash", ""),
            "duration_ms": r.get("duration_ms", 0),
            "origin": r.get("origin", ""),
        }
        # Parse args back
        if r.get("args_json"):
            try:
                event["args"] = json.loads(r["args_json"])
            except (json.JSONDecodeError, TypeError):
                event["args"] = {}
        # Parse delta as model_changes_summary
        if r.get("delta_json"):
            try:
                event["model_changes_summary"] = json.loads(r["delta_json"])
            except (json.JSONDecodeError, TypeError):
                pass
        # Error info
        if r.get("error_json"):
            try:
                event["error"] = json.loads(r["error_json"])
            except (json.JSONDecodeError, TypeError):
                event["error"] = r["error_json"]
        if r.get("error_code"):
            event.setdefault("result_summary", {})["error_code"] = r["error_code"]
        return event

    # ------------------------------------------------------------------
    # Connections CRUD
    # ------------------------------------------------------------------

    def save_connection(self, conn_data: dict[str, Any]) -> None:
        """Insert or replace a connection row."""
        self._conn.execute(
            """INSERT OR REPLACE INTO connections
               (id, name, transport, enabled, command, args_json, env_json,
                url, headers_json, auth_type, auth_credential_enc,
                server_name, server_version, discovered_tools_json,
                created_at, updated_at, last_connected_at, last_error, status)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
            (
                conn_data["id"],
                conn_data["name"],
                conn_data.get("transport", "stdio"),
                1 if conn_data.get("enabled", True) else 0,
                conn_data.get("command", ""),
                json.dumps(conn_data.get("args", [])),
                json.dumps(conn_data.get("env", {})),
                conn_data.get("url", ""),
                json.dumps(conn_data.get("headers", {})),
                conn_data.get("auth_type", "none"),
                conn_data.get("auth_credential_enc", ""),
                conn_data.get("server_name", ""),
                conn_data.get("server_version", ""),
                conn_data.get("discovered_tools_json", "[]"),
                conn_data.get("created_at", _now_iso()),
                conn_data.get("updated_at", _now_iso()),
                conn_data.get("last_connected_at"),
                conn_data.get("last_error"),
                conn_data.get("status", "disconnected"),
            ),
        )
        self._conn.commit()

    def load_connections(self) -> list[dict[str, Any]]:
        """Load all connection rows."""
        rows = self._conn.execute(
            "SELECT * FROM connections ORDER BY name"
        ).fetchall()
        return [self._row_to_connection(r) for r in rows]

    def load_connection(self, conn_id: str) -> Optional[dict[str, Any]]:
        """Load a single connection by ID."""
        row = self._conn.execute(
            "SELECT * FROM connections WHERE id = ?", (conn_id,)
        ).fetchone()
        return self._row_to_connection(row) if row else None

    def delete_connection(self, conn_id: str) -> bool:
        """Delete a connection by ID. Returns True if a row was deleted."""
        cur = self._conn.execute(
            "DELETE FROM connections WHERE id = ?", (conn_id,)
        )
        self._conn.commit()
        return cur.rowcount > 0

    def update_connection(self, conn_id: str, updates: dict[str, Any]) -> bool:
        """Update specific fields on a connection. Returns True if found."""
        # Map Python-style fields to SQL column names
        column_map = {
            "name": "name",
            "transport": "transport",
            "enabled": "enabled",
            "command": "command",
            "args": "args_json",
            "env": "env_json",
            "url": "url",
            "headers": "headers_json",
            "auth_type": "auth_type",
            "auth_credential_enc": "auth_credential_enc",
            "server_name": "server_name",
            "server_version": "server_version",
            "discovered_tools_json": "discovered_tools_json",
            "status": "status",
            "last_error": "last_error",
            "last_connected_at": "last_connected_at",
        }
        sets: list[str] = []
        params: list[Any] = []
        for key, value in updates.items():
            col = column_map.get(key)
            if col is None:
                continue
            if key == "enabled":
                value = 1 if value else 0
            elif key in ("args", "env", "headers"):
                value = json.dumps(value)
            sets.append(f"{col} = ?")
            params.append(value)

        if not sets:
            return False

        sets.append("updated_at = ?")
        params.append(_now_iso())
        params.append(conn_id)

        sql = f"UPDATE connections SET {', '.join(sets)} WHERE id = ?"
        cur = self._conn.execute(sql, params)
        self._conn.commit()
        return cur.rowcount > 0

    @staticmethod
    def _row_to_connection(row: sqlite3.Row) -> dict[str, Any]:
        """Convert a connections row to a dict."""
        r = dict(row)
        # Parse JSON fields
        for field, key in [("args_json", "args"), ("env_json", "env"), ("headers_json", "headers")]:
            try:
                r[key] = json.loads(r.pop(field, "{}"))
            except (json.JSONDecodeError, TypeError):
                r[key] = {} if key != "args" else []
                r.pop(field, None)
        r["enabled"] = bool(r.get("enabled", 1))
        return r

    def close(self) -> None:
        """Close the database connection."""
        self._conn.close()


def _now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()
