"""Handler for orchestrator.suggest_workflows tool."""

from __future__ import annotations

from typing import Any

from orchestrator.dispatcher.result import ToolResult


async def execute(args: dict[str, Any], **kwargs) -> ToolResult:
    """Detect recurring patterns and suggest named workflows."""
    from orchestrator.server import _event_store, _audit_log

    _log = _event_store or _audit_log
    if _log is None:
        return ToolResult.fail("HANDLER_ERROR", "Audit log not available")

    from orchestrator.telemetry.pattern_miner import PatternMiner

    lookback_days = args.get("lookback_days", 30)
    min_occurrences = args.get("min_occurrences", 3)

    miner = PatternMiner(_log)
    patterns = miner.find_patterns(
        min_occurrences=min_occurrences,
        lookback_days=lookback_days,
    )

    suggestions = []
    for p in patterns[:5]:  # Limit to top 5
        suggestions.append({
            "sequence": p.sequence,
            "occurrences": p.occurrences,
            "description": p.description,
            "example_args": p.example_args,
        })

    return ToolResult.ok({
        "suggestions": suggestions,
        "patterns_found": len(patterns),
    })
