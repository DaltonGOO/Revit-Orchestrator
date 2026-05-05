"""Stub handler for dynamo.run_graph_interactive.

The real work happens in DynamoAdapter, which sends a custom pipe message to
the C# add-in to open the Run Graph dialog and waits for the user's result.
This handler is only here so the dispatcher's handler-loading step finds
something for the tool — its return value is never used.
"""

from __future__ import annotations

from typing import Any

from ..dispatcher.result import ToolResult


async def execute(args: dict[str, Any], **kwargs: Any) -> ToolResult:
    return ToolResult.ok({
        "message": "Delegated to DynamoAdapter for interactive dispatch",
    })
