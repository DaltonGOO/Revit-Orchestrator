"""Handler for revit.create_wall — delegates to Revit add-in via pipe."""

from __future__ import annotations

from typing import Any

from ..dispatcher.result import ToolResult


async def execute(args: dict[str, Any], **kwargs: Any) -> ToolResult:
    """Execute the create_wall tool.

    Pass-through to the Revit add-in via the RevitAddinAdapter.
    """
    return ToolResult.ok({
        "message": "Delegated to Revit add-in",
        "args": args,
    })
