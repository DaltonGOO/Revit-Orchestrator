"""Handler for revit.get_element_info — delegates to Revit add-in via pipe."""

from __future__ import annotations

from typing import Any

from ..dispatcher.result import ToolResult


async def execute(args: dict[str, Any], **kwargs: Any) -> ToolResult:
    """Execute the get_element_info tool.

    This handler is a pass-through — the RevitAddinAdapter sends the call
    over the named pipe to the C# add-in, which does the actual Revit API work.
    The adapter calls this handler, but for revit-adapter tools the real execution
    happens on the C# side. This handler just formats/validates before sending.
    """
    # For revit adapter tools, the adapter handles the pipe communication.
    # This handler can add any Python-side pre/post processing if needed.
    return ToolResult.ok({
        "element_id": args["element_id"],
        "message": "Delegated to Revit add-in",
    })
