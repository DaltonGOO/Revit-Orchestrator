"""Handler for flow.create_walls_from_lines — multi-step wall creation workflow."""

from __future__ import annotations

from typing import Any

from ..dispatcher.result import ToolResult


async def execute(args: dict[str, Any], dispatcher: Any = None, **kwargs: Any) -> ToolResult:
    """Create multiple walls from line segments.

    This workflow handler iterates over the line segments and calls
    revit.create_wall for each one via the dispatcher.
    """
    if dispatcher is None:
        return ToolResult.fail(
            "HANDLER_ERROR",
            "Workflow handler requires a dispatcher for sub-tool calls",
        )

    lines = args["lines"]
    height = args["height"]
    wall_type = args.get("wall_type")

    element_ids: list[int] = []
    errors: list[str] = []

    for i, line in enumerate(lines):
        wall_args: dict[str, Any] = {
            "start_point": line["start"],
            "end_point": line["end"],
            "height": height,
        }
        if wall_type:
            wall_args["wall_type"] = wall_type

        result = await dispatcher.dispatch("revit.create_wall", wall_args)
        if result.success:
            eid = result.data.get("element_id")
            if eid is not None:
                element_ids.append(eid)
        else:
            errors.append(
                f"Wall {i}: {result.error_code} - {result.error_message}"
            )

    return ToolResult.ok({
        "created_count": len(element_ids),
        "element_ids": element_ids,
        "errors": errors,
    })
