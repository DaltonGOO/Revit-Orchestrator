"""Replay tool executions from the audit log."""

from __future__ import annotations

import logging
from typing import Any

from ..dispatcher.result import ToolResult

logger = logging.getLogger(__name__)


class RunReplayer:
    """Replays a past run from the audit log.

    By default replays in dry-run mode for safety.
    """

    def __init__(self, audit_log: Any, dispatcher: Any) -> None:
        self._audit_log = audit_log
        self._dispatcher = dispatcher

    async def replay(
        self, correlation_id: str, dry_run: bool = True
    ) -> list[ToolResult]:
        """Replay all tool calls from a correlation group.

        Args:
            correlation_id: The correlation ID to replay.
            dry_run: If True, runs in dry-run mode (no actual execution).

        Returns:
            List of ToolResults from the replay.
        """
        # Prefer get_episode_events() if available (SqliteEventStore)
        if hasattr(self._audit_log, "get_episode_events"):
            events = self._audit_log.get_episode_events(correlation_id)
            tool_calls = [
                e for e in events
                if e.get("status") == "started" and e.get("tool_name")
            ]
        else:
            events = self._audit_log.read_events(correlation_id=correlation_id)
            tool_calls = [
                e for e in events
                if e.get("event_type") == "started"
            ]

        if not tool_calls:
            return [ToolResult.fail(
                "NO_EVENTS_FOUND",
                f"No tool calls found for correlation_id: {correlation_id}",
            )]

        results: list[ToolResult] = []
        for event in tool_calls:
            tool_name = event.get("tool_name", "")
            args = event.get("args", {})

            result = await self._dispatcher.dispatch(
                tool_name, args, dry_run=dry_run
            )
            results.append(result)

        return results
