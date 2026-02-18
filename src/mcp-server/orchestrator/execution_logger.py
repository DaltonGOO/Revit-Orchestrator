"""Execution event logging for tool calls during agentic loops."""

from __future__ import annotations

import hashlib
import json
import logging
import time
import uuid
from typing import Any

from .dispatcher.result import ToolResult
from .pipe.connection import PipeConnection
from .pipe.protocol import make_execution_event

logger = logging.getLogger(__name__)


class ExecutionLogger:
    """Logs tool execution events to the C# client over the pipe connection.

    Tracks a correlation_id for each agentic loop and sequence numbers
    within that loop to allow the UI to group and order related events.
    Optionally writes to a persistent AuditLog or SqliteEventStore.
    """

    def __init__(
        self,
        connection: PipeConnection,
        audit_log: Any | None = None,
        origin: str = "chat",
    ) -> None:
        self._connection = connection
        self._audit_log = audit_log
        self._origin = origin
        self._correlation_id: str | None = None
        self._sequence: int = 0
        self._start_times: dict[str, float] = {}
        self._run_context: dict[str, Any] = {}

    def set_run_context(self, context: dict[str, Any]) -> None:
        """Set the current Revit run context (doc_guid, doc_title, etc.)."""
        self._run_context = context

    def start_correlation(self) -> str:
        """Start a new correlation group (called at the start of an agentic loop).

        Returns the new correlation_id.
        """
        self._correlation_id = str(uuid.uuid4())
        self._sequence = 0
        self._start_times.clear()
        logger.debug("Started new correlation: %s", self._correlation_id)

        # If the store supports episodes, start one
        if hasattr(self._audit_log, "start_episode"):
            try:
                self._audit_log.start_episode(
                    episode_id=self._correlation_id,
                    origin=self._origin,
                    context=self._run_context,
                )
            except Exception:
                logger.exception("Failed to start episode in event store")

        return self._correlation_id

    @property
    def correlation_id(self) -> str | None:
        """Current correlation ID, or None if not in an agentic loop."""
        return self._correlation_id

    @staticmethod
    def _hash_args(args: dict[str, Any]) -> str:
        """Compute a short SHA256 hash of the arguments for deduplication."""
        raw = json.dumps(args, sort_keys=True, default=str)
        return hashlib.sha256(raw.encode()).hexdigest()[:16]

    def _write_audit_event(
        self,
        event_type: str,
        tool_name: str,
        args: dict[str, Any],
        event_id: str,
        result: ToolResult | None = None,
        duration_ms: int = 0,
        error: str | None = None,
    ) -> None:
        """Write an event to the persistent audit log / event store if available."""
        if self._audit_log is None:
            return

        # Extract model changes data
        delta_json = None
        error_code = None
        connection_info = None
        if result is not None:
            created = result.data.get("model_changes", {}).get("created", [])
            modified = result.data.get("model_changes", {}).get("modified", [])
            deleted = result.data.get("model_changes", {}).get("deleted", [])
            mcs = {
                "created": len(created),
                "modified": len(modified),
                "deleted": len(deleted),
            }
            if any(mcs.values()):
                delta_json = json.dumps(mcs, default=str)
            if not result.success:
                error_code = result.error_code
            # Extract MCP connection traceability
            connection_info = result.data.get("_connection")

        # Use episode-aware API if available (SqliteEventStore)
        if hasattr(self._audit_log, "log_step"):
            try:
                cid = self._correlation_id or ""
                if event_type == "started":
                    self._audit_log.log_step(
                        episode_id=cid,
                        event_id=event_id,
                        seq=self._sequence,
                        tool_name=tool_name,
                        args=args,
                        args_hash=self._hash_args(args),
                        origin=self._origin,
                    )
                elif event_type in ("completed", "failed"):
                    # Build entity targets from model_changes if available
                    targets_json = None
                    if result is not None and hasattr(self._audit_log, "blob_store"):
                        mc = result.data.get("model_changes", {})
                        doc_guid = self._run_context.get("doc_guid", "")
                        if mc and doc_guid:
                            try:
                                from .store.entity_tracker import EntityTracker
                                tracker = EntityTracker(self._audit_log)
                                targets_json = tracker.build_targets_json(doc_guid, mc)
                            except Exception:
                                logger.debug("Entity tracking failed (non-fatal)")

                    # Build context with connection traceability
                    context_json = None
                    if connection_info:
                        context_json = json.dumps({
                            "connection_id": connection_info.get("connection_id", ""),
                            "connection_name": connection_info.get("connection_name", ""),
                            "original_tool_name": connection_info.get("original_tool_name", ""),
                        }, default=str)

                    self._audit_log.update_step(
                        event_id=event_id,
                        status=event_type,
                        duration_ms=duration_ms,
                        delta_json=delta_json,
                        error_code=error_code,
                        error_json=json.dumps(error) if error else None,
                        targets_json=targets_json,
                    )
                    # Write connection traceability to context column
                    if context_json and hasattr(self._audit_log, '_conn'):
                        try:
                            self._audit_log._conn.execute(
                                "UPDATE events SET context_json = ? WHERE id = ?",
                                (context_json, event_id),
                            )
                            self._audit_log._conn.commit()
                        except Exception:
                            logger.debug("Failed to write connection context", exc_info=True)
            except Exception:
                logger.exception("Failed to write to event store")
            return

        # Fallback: original AuditLog flat event format
        event: dict[str, Any] = {
            "event_id": event_id,
            "correlation_id": self._correlation_id or "",
            "sequence": self._sequence,
            "event_type": event_type,
            "tool_name": tool_name,
            "args_hash": self._hash_args(args),
            "args": args,
            "duration_ms": duration_ms,
            "origin": self._origin,
        }

        if result is not None:
            created = result.data.get("model_changes", {}).get("created", [])
            modified = result.data.get("model_changes", {}).get("modified", [])
            deleted = result.data.get("model_changes", {}).get("deleted", [])
            event["result_summary"] = {
                "success": result.success,
                "error_code": result.error_code,
                "created_count": len(created),
                "modified_count": len(modified),
                "deleted_count": len(deleted),
            }
            event["model_changes_summary"] = {
                "created": len(created),
                "modified": len(modified),
                "deleted": len(deleted),
            }

        if error:
            event["error"] = error

        try:
            self._audit_log.log_event(event)
        except Exception:
            logger.exception("Failed to write audit event")

    async def log_started(self, tool_name: str, args: dict[str, Any]) -> str:
        """Log a tool execution started event.

        Args:
            tool_name: Name of the tool being called.
            args: Arguments passed to the tool.

        Returns:
            The event_id for this execution (use with log_completed/log_failed).
        """
        if self._correlation_id is None:
            self._correlation_id = str(uuid.uuid4())

        event_id = str(uuid.uuid4())
        self._sequence += 1
        self._start_times[event_id] = time.perf_counter()

        message = make_execution_event(
            event_type="started",
            tool_name=tool_name,
            args=args,
            correlation_id=self._correlation_id,
            sequence=self._sequence,
            event_id=event_id,
            origin=self._origin,
        )

        try:
            await self._connection.send(message)
            logger.debug(
                "Logged execution started: tool=%s, event_id=%s, correlation=%s, seq=%d",
                tool_name,
                event_id,
                self._correlation_id,
                self._sequence,
            )
        except ConnectionError:
            logger.warning("Failed to log execution started: connection lost")
        except Exception:
            logger.exception("Unexpected error logging execution started")

        self._write_audit_event("started", tool_name, args, event_id)
        return event_id

    async def log_completed(
        self,
        event_id: str,
        result: ToolResult,
        tool_name: str,
        args: dict[str, Any],
    ) -> None:
        """Log a tool execution completed event."""
        duration_ms = self._calculate_duration(event_id)

        message = make_execution_event(
            event_type="completed",
            tool_name=tool_name,
            args=args,
            correlation_id=self._correlation_id or "",
            sequence=self._sequence,
            event_id=event_id,
            result=result.to_dict(),
            duration_ms=duration_ms,
            origin=self._origin,
        )

        try:
            await self._connection.send(message)
            logger.debug(
                "Logged execution completed: event_id=%s, duration=%dms",
                event_id,
                duration_ms,
            )
        except ConnectionError:
            logger.warning("Failed to log execution completed: connection lost")
        except Exception:
            logger.exception("Unexpected error logging execution completed")

        self._write_audit_event(
            "completed", tool_name, args, event_id,
            result=result, duration_ms=duration_ms,
        )

    async def log_failed(
        self,
        event_id: str,
        error: str,
        tool_name: str,
        args: dict[str, Any],
    ) -> None:
        """Log a tool execution failed event."""
        duration_ms = self._calculate_duration(event_id)

        message = make_execution_event(
            event_type="failed",
            tool_name=tool_name,
            args=args,
            correlation_id=self._correlation_id or "",
            sequence=self._sequence,
            event_id=event_id,
            duration_ms=duration_ms,
            error=error,
            origin=self._origin,
        )

        try:
            await self._connection.send(message)
            logger.debug(
                "Logged execution failed: event_id=%s, error=%s, duration=%dms",
                event_id,
                error,
                duration_ms,
            )
        except ConnectionError:
            logger.warning("Failed to log execution failed: connection lost")
        except Exception:
            logger.exception("Unexpected error logging execution failed")

        self._write_audit_event(
            "failed", tool_name, args, event_id,
            duration_ms=duration_ms, error=error,
        )

    def log_correlation_summary(
        self,
        correlation_id: str,
        total_usage: dict[str, Any] | None = None,
        outcome: str = "completed",
    ) -> None:
        """Log an end-of-loop summary event to the audit log / event store."""
        if self._audit_log is None:
            return

        # Use episode-aware API if available
        if hasattr(self._audit_log, "end_episode"):
            try:
                self._audit_log.end_episode(
                    correlation_id,
                    outcome=outcome,
                    total_usage=total_usage,
                )
            except Exception:
                logger.exception("Failed to end episode in event store")
            return

        # Fallback: original AuditLog format
        event = {
            "event_id": str(uuid.uuid4()),
            "correlation_id": correlation_id,
            "event_type": "correlation_summary",
            "tool_name": "",
            "sequence": self._sequence,
        }
        if total_usage:
            event["total_usage"] = total_usage

        try:
            self._audit_log.log_event(event)
        except Exception:
            logger.exception("Failed to write correlation summary")

    def _calculate_duration(self, event_id: str) -> int:
        """Calculate duration in milliseconds since the event started."""
        start_time = self._start_times.pop(event_id, None)
        if start_time is None:
            return 0
        return int((time.perf_counter() - start_time) * 1000)
