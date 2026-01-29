"""Handler for dynamo.run_graph — runs Dynamo graphs via Revit add-in."""

from __future__ import annotations

from typing import Any

from ..dispatcher.result import ToolResult


async def execute(args: dict[str, Any], **kwargs: Any) -> ToolResult:
    """Execute a Dynamo graph.

    The actual execution is handled by the Revit add-in's Dynamo integration.
    This handler prepares the request to be sent over the pipe.
    """
    graph_path = args["graph_path"]
    inputs = args.get("inputs", {})

    return ToolResult.ok({
        "message": f"Delegated Dynamo graph execution: {graph_path}",
        "graph_path": graph_path,
        "inputs": inputs,
    })
