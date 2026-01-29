"""Adapter that sends tool calls over named pipe to the C# Revit add-in."""

from __future__ import annotations

import asyncio
import logging
from typing import Any

from .base import BaseAdapter
from ..dispatcher.result import ToolResult
from ..pipe.protocol import make_tool_call, encode_message

logger = logging.getLogger(__name__)


class RevitAddinAdapter(BaseAdapter):
    """Sends tool calls to the Revit add-in over the named pipe."""

    def __init__(self) -> None:
        self._connection: Any | None = None

    @property
    def name(self) -> str:
        return "revit"

    def set_connection(self, connection: Any) -> None:
        """Set the active pipe connection."""
        self._connection = connection

    async def execute(
        self, tool_name: str, args: dict[str, Any], handler: Any
    ) -> ToolResult:
        """Send a tool call over the pipe and wait for the result."""
        if self._connection is None:
            return ToolResult.fail(
                "ADAPTER_NOT_AVAILABLE",
                "Revit add-in is not connected",
            )

        message = make_tool_call(tool_name, args)
        try:
            result = await self._connection.send_and_wait(message)
            payload = result.get("payload", {})
            if payload.get("success"):
                return ToolResult.ok(
                    payload.get("data", {}),
                    duration_ms=payload.get("duration_ms", 0),
                )
            else:
                error = payload.get("error", {})
                return ToolResult.fail(
                    error.get("code", "REVIT_API_ERROR"),
                    error.get("message", "Unknown error from Revit"),
                    duration_ms=payload.get("duration_ms", 0),
                )
        except asyncio.TimeoutError:
            return ToolResult.fail("PIPE_TIMEOUT", "Revit add-in did not respond in time")
        except ConnectionError:
            self._connection = None
            return ToolResult.fail("PIPE_DISCONNECTED", "Lost connection to Revit add-in")

    async def is_available(self) -> bool:
        return self._connection is not None
