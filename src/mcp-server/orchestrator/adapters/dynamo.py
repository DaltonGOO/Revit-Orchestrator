"""Adapter that runs Dynamo graphs."""

from __future__ import annotations

import logging
from typing import Any

from .base import BaseAdapter
from ..dispatcher.result import ToolResult

logger = logging.getLogger(__name__)


class DynamoAdapter(BaseAdapter):
    """Executes tools by running Dynamo graphs via the Revit add-in.

    Dynamo tools are executed by sending the tool call over the pipe to the
    C# Revit add-in, which handles the actual Dynamo API interaction.
    """

    def __init__(self) -> None:
        self._revit_adapter: Any | None = None

    @property
    def name(self) -> str:
        return "dynamo"

    def set_revit_adapter(self, revit_adapter: Any) -> None:
        """Set the Revit adapter used to communicate with the add-in."""
        self._revit_adapter = revit_adapter

    async def execute(
        self, tool_name: str, args: dict[str, Any], handler: Any
    ) -> ToolResult:
        """Execute a Dynamo graph via the Revit add-in.

        This sends the tool call over the pipe to C#, where the actual
        Dynamo execution happens. All Dynamo tools are normalized to
        'dynamo.run_graph' since they all execute graphs.
        """
        if self._revit_adapter is None:
            return ToolResult.fail(
                "ADAPTER_NOT_AVAILABLE",
                "Revit add-in is not connected",
            )

        try:
            # All Dynamo tools map to the run_graph command on C# side
            # The specific tool name is preserved in the original call for logging
            normalized_tool = "dynamo.run_graph"

            # Send the tool call to C# via the Revit adapter
            result = await self._revit_adapter.execute(normalized_tool, args, handler)
            return result
        except Exception as e:
            logger.exception("Dynamo execution error for %s", tool_name)
            return ToolResult.fail("DYNAMO_EXECUTION_ERROR", str(e))

    async def is_available(self) -> bool:
        # Dynamo runs through the Revit add-in
        if self._revit_adapter is not None:
            return await self._revit_adapter.is_available()
        return False
