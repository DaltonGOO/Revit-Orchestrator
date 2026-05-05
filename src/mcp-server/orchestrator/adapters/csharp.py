"""Adapter that runs C# tool scripts.

Same shape as the Dynamo adapter: any ``csharp.*`` tool name is normalised
to ``csharp.run_script`` and forwarded over the pipe to the C# add-in.
That lets us add wrapper tool definitions (e.g. ``csharp.count_walls``)
that bake in a specific ``script_path`` so the LLM never has to guess
where the script lives — the C# side just sees ``csharp.run_script`` with
the right path in args.
"""

from __future__ import annotations

import logging
from typing import Any

from .base import BaseAdapter
from ..dispatcher.result import ToolResult

logger = logging.getLogger(__name__)


class CSharpAdapter(BaseAdapter):
    """Forwards C# script tools to the Revit add-in's IRevitCommand."""

    def __init__(self) -> None:
        self._revit_adapter: Any | None = None

    @property
    def name(self) -> str:
        return "csharp"

    def set_revit_adapter(self, revit_adapter: Any) -> None:
        """Set the Revit adapter used to communicate with the add-in."""
        self._revit_adapter = revit_adapter

    async def execute(
        self, tool_name: str, args: dict[str, Any], handler: Any
    ) -> ToolResult:
        """Send the call over the pipe to the C# Roslyn host."""
        if self._revit_adapter is None:
            return ToolResult.fail(
                "ADAPTER_NOT_AVAILABLE",
                "Revit add-in is not connected",
            )

        try:
            # Every csharp.* tool maps to the same C# command — wrapper
            # defs supply the right ``script_path`` (and any default
            # ``arguments``) via JSON Schema defaults.
            normalized = "csharp.run_script"
            return await self._revit_adapter.execute(normalized, args, handler)
        except Exception as e:
            logger.exception("C# script error for %s", tool_name)
            return ToolResult.fail("CSHARP_EXECUTION_ERROR", str(e))

    async def is_available(self) -> bool:
        if self._revit_adapter is not None:
            return await self._revit_adapter.is_available()
        return False
