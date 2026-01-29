"""Adapter that runs Dynamo graphs."""

from __future__ import annotations

import logging
from typing import Any

from .base import BaseAdapter
from ..dispatcher.result import ToolResult

logger = logging.getLogger(__name__)


class DynamoAdapter(BaseAdapter):
    """Executes tools by running Dynamo graphs via the Revit add-in."""

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
        """Execute a Dynamo graph via the handler module."""
        try:
            result = await handler.execute(args)
            return result
        except Exception as e:
            logger.exception("Dynamo execution error for %s", tool_name)
            return ToolResult.fail("DYNAMO_EXECUTION_ERROR", str(e))

    async def is_available(self) -> bool:
        # Dynamo runs through the Revit add-in
        if self._revit_adapter is not None:
            return await self._revit_adapter.is_available()
        return False
