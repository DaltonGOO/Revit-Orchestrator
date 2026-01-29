"""Adapter that executes composed multi-step workflow tools."""

from __future__ import annotations

import logging
from typing import Any

from .base import BaseAdapter
from ..dispatcher.result import ToolResult

logger = logging.getLogger(__name__)


class WorkflowAdapter(BaseAdapter):
    """Executes multi-step workflow tools.

    Workflow handlers orchestrate calls to other tools (revit, pyrevit, dynamo)
    through the dispatcher.
    """

    def __init__(self) -> None:
        self._dispatcher: Any | None = None

    @property
    def name(self) -> str:
        return "workflow"

    def set_dispatcher(self, dispatcher: Any) -> None:
        """Set the dispatcher for sub-tool calls within workflows."""
        self._dispatcher = dispatcher

    async def execute(
        self, tool_name: str, args: dict[str, Any], handler: Any
    ) -> ToolResult:
        """Execute a workflow via the handler module.

        The handler receives the dispatcher so it can make sub-tool calls.
        """
        try:
            result = await handler.execute(args, dispatcher=self._dispatcher)
            return result
        except Exception as e:
            logger.exception("Workflow error for %s", tool_name)
            return ToolResult.fail("HANDLER_ERROR", str(e))

    async def is_available(self) -> bool:
        return self._dispatcher is not None
