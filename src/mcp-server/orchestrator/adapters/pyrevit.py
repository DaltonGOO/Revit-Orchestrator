"""Adapter that executes pyRevit/Python scripts inside Revit."""

from __future__ import annotations

import logging
from typing import Any

from .base import BaseAdapter
from ..dispatcher.result import ToolResult

logger = logging.getLogger(__name__)


class PyRevitAdapter(BaseAdapter):
    """Executes pyRevit/Python scripts inside Revit via the C# add-in.

    Scripts are executed in-process using IronPython, with full model
    change tracking. This requires Revit to be running and connected.
    """

    def __init__(self) -> None:
        self._revit_adapter: Any | None = None

    @property
    def name(self) -> str:
        return "pyrevit"

    def set_revit_adapter(self, revit_adapter: Any) -> None:
        """Set the Revit adapter used to communicate with the add-in."""
        self._revit_adapter = revit_adapter

    async def execute(
        self, tool_name: str, args: dict[str, Any], handler: Any
    ) -> ToolResult:
        """Execute a Python script inside Revit.

        This sends the tool call over the pipe to C#, where the script
        is executed using IronPython with model change tracking.
        """
        if self._revit_adapter is None:
            return ToolResult.fail(
                "ADAPTER_NOT_AVAILABLE",
                "Revit add-in is not connected",
            )

        try:
            # All pyRevit tools map to the run_script command on C# side
            normalized_tool = "pyrevit.run_script"

            # Send the tool call to C# via the Revit adapter
            result = await self._revit_adapter.execute(normalized_tool, args, handler)
            return result
        except Exception as e:
            logger.exception("pyRevit script error for %s", tool_name)
            return ToolResult.fail("PYREVIT_SCRIPT_ERROR", str(e))

    async def is_available(self) -> bool:
        # pyRevit scripts run through the Revit add-in
        if self._revit_adapter is not None:
            return await self._revit_adapter.is_available()
        return False
