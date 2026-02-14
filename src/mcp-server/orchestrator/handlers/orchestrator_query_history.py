"""Handler for orchestrator.query_history tool."""

from __future__ import annotations

from datetime import datetime, timezone
from typing import Any

from orchestrator.dispatcher.result import ToolResult


async def execute(args: dict[str, Any], **kwargs) -> ToolResult:
    """Query execution history from the audit log."""
    from orchestrator.server import _event_store, _audit_log

    _log = _event_store or _audit_log
    if _log is None:
        return ToolResult.fail("HANDLER_ERROR", "Audit log not available")

    date_str = args.get("date")
    tool_name = args.get("tool_name")
    correlation_id = args.get("correlation_id")
    limit = args.get("limit", 20)

    date = None
    if date_str:
        try:
            date = datetime.strptime(date_str, "%Y-%m-%d").replace(tzinfo=timezone.utc)
        except ValueError:
            return ToolResult.fail(
                "SCHEMA_VALIDATION_FAILED",
                f"Invalid date format: {date_str}. Expected YYYY-MM-DD.",
            )

    events = _log.read_events(
        date=date,
        tool_name=tool_name,
        correlation_id=correlation_id,
        limit=limit,
    )

    # Build summaries
    summaries = []
    for event in events:
        summaries.append({
            "event_id": event.get("event_id", ""),
            "event_type": event.get("event_type", ""),
            "tool_name": event.get("tool_name", ""),
            "correlation_id": event.get("correlation_id", ""),
            "timestamp": event.get("timestamp", ""),
            "duration_ms": event.get("duration_ms", 0),
            "success": event.get("result_summary", {}).get("success"),
        })

    return ToolResult.ok({
        "events": summaries,
        "total_count": len(summaries),
    })
