"""Handler for orchestrator.replay_run tool."""

from __future__ import annotations

from typing import Any

from orchestrator.dispatcher.result import ToolResult


async def execute(args: dict[str, Any], **kwargs) -> ToolResult:
    """Replay a previous run from the audit log."""
    dispatcher = kwargs.get("dispatcher")
    if dispatcher is None:
        return ToolResult.fail("HANDLER_ERROR", "No dispatcher available")

    from orchestrator.server import _event_store, _audit_log

    _log = _event_store or _audit_log
    if _log is None:
        return ToolResult.fail("HANDLER_ERROR", "Audit log not available")

    from orchestrator.telemetry.replay import RunReplayer

    correlation_id = args.get("correlation_id", "")
    dry_run = args.get("dry_run", True)

    replayer = RunReplayer(_log, dispatcher._dispatcher)
    results = await replayer.replay(correlation_id, dry_run=dry_run)

    return ToolResult.ok({
        "results": [r.to_dict() for r in results],
        "total_steps": len(results),
        "dry_run": dry_run,
    })
