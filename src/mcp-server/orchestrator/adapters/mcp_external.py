"""Adapter that routes tool calls to external MCP servers via ConnectionManager."""

from __future__ import annotations

import asyncio
import logging
from typing import Any

from .base import BaseAdapter
from ..dispatcher.result import ToolResult

logger = logging.getLogger(__name__)


class McpExternalAdapter(BaseAdapter):
    """Routes tool calls to external MCP servers via the ConnectionManager."""

    def __init__(self, manager: Any) -> None:
        self._manager = manager

    @property
    def name(self) -> str:
        return "mcp"

    async def execute(
        self,
        tool_name: str,
        args: dict[str, Any],
        handler: Any,
        execution_mode: str = "headless",
    ) -> ToolResult:
        """Execute a tool call on an external MCP server."""
        conn = self._manager.get_connection_for_tool(tool_name)
        if conn is None:
            return ToolResult.fail(
                "CONNECTION_NOT_FOUND",
                f"No connection found for tool '{tool_name}'",
            )

        if conn.status != "connected":
            return ToolResult.fail(
                "CONNECTION_UNAVAILABLE",
                f"Connection '{conn.name}' is not connected (status: {conn.status})",
            )

        # Look up the original tool name from registry metadata
        tool_def = self._manager._registry.get(tool_name)
        if tool_def is None:
            return ToolResult.fail(
                "TOOL_NOT_FOUND",
                f"Tool '{tool_name}' not found in registry",
            )

        metadata = tool_def.get("metadata", {})
        original_name = metadata.get("original_tool_name", "")
        if not original_name:
            return ToolResult.fail(
                "TOOL_METADATA_MISSING",
                f"Tool '{tool_name}' is missing original_tool_name metadata",
            )

        try:
            result = await asyncio.wait_for(
                self._manager._pool.call_tool(conn.id, original_name, args),
                timeout=30.0,
            )
        except asyncio.TimeoutError:
            return ToolResult.fail(
                "MCP_TIMEOUT",
                f"Tool call to '{original_name}' on '{conn.name}' timed out after 30s",
            )
        except Exception as e:
            return ToolResult.fail(
                "MCP_ERROR",
                f"Error calling '{original_name}' on '{conn.name}': {e}",
            )

        if result.get("success"):
            data = result.get("data", {})
            # Attach connection traceability
            data["_connection"] = {
                "connection_id": conn.id,
                "connection_name": conn.name,
                "original_tool_name": original_name,
                "server_name": conn.server_name,
                "server_version": conn.server_version,
            }
            return ToolResult.ok(data)
        else:
            return ToolResult.fail(
                "MCP_TOOL_ERROR",
                result.get("error", "Unknown error from external MCP server"),
            )

    async def is_available(self) -> bool:
        """Always returns True — per-connection availability is checked in execute()."""
        return True
