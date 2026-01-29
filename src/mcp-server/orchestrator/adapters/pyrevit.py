"""Adapter that invokes pyRevit CLI scripts."""

from __future__ import annotations

import asyncio
import logging
from typing import Any

from .base import BaseAdapter
from ..dispatcher.result import ToolResult

logger = logging.getLogger(__name__)


class PyRevitAdapter(BaseAdapter):
    """Executes tools by invoking pyRevit CLI scripts."""

    @property
    def name(self) -> str:
        return "pyrevit"

    async def execute(
        self, tool_name: str, args: dict[str, Any], handler: Any
    ) -> ToolResult:
        """Execute a pyRevit script via the handler module."""
        try:
            result = await handler.execute(args)
            return result
        except Exception as e:
            logger.exception("pyRevit script error for %s", tool_name)
            return ToolResult.fail("PYREVIT_SCRIPT_ERROR", str(e))

    async def is_available(self) -> bool:
        # Check if pyRevit CLI is accessible
        try:
            proc = await asyncio.create_subprocess_exec(
                "pyrevit", "--version",
                stdout=asyncio.subprocess.DEVNULL,
                stderr=asyncio.subprocess.DEVNULL,
            )
            await proc.wait()
            return proc.returncode == 0
        except FileNotFoundError:
            return False
