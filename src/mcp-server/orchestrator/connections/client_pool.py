"""MCP client pool — manages active ClientSession instances to external servers."""

from __future__ import annotations

import asyncio
import logging
from contextlib import AsyncExitStack
from typing import Any

from mcp import ClientSession
from mcp.client.stdio import stdio_client, StdioServerParameters
from mcp.client.sse import sse_client
from mcp.client.streamable_http import streamablehttp_client

logger = logging.getLogger(__name__)


class McpClientPool:
    """Manages active MCP ClientSession instances for external connections.

    Each connection gets its own transport and session.  A failing connection
    doesn't crash others.
    """

    def __init__(self) -> None:
        self._sessions: dict[str, ClientSession] = {}
        self._stacks: dict[str, AsyncExitStack] = {}
        self._init_results: dict[str, Any] = {}

    async def connect(
        self,
        conn_id: str,
        transport: str,
        *,
        command: str = "",
        args: list[str] | None = None,
        env: dict[str, str] | None = None,
        url: str = "",
        headers: dict[str, str] | None = None,
    ) -> ClientSession:
        """Create a transport + session for a connection.

        Returns the initialized ClientSession.
        """
        if conn_id in self._sessions:
            await self.disconnect(conn_id)

        stack = AsyncExitStack()
        self._stacks[conn_id] = stack

        try:
            if transport == "stdio":
                params = StdioServerParameters(
                    command=command,
                    args=args or [],
                    env=env if env else None,
                )
                read_stream, write_stream = await stack.enter_async_context(
                    stdio_client(params)
                )
            elif transport == "sse":
                read_stream, write_stream = await stack.enter_async_context(
                    sse_client(url, headers=headers or {})
                )
            elif transport == "streamable_http":
                read_stream, write_stream = await stack.enter_async_context(
                    streamablehttp_client(url, headers=headers or {})
                )
            else:
                raise ValueError(f"Unknown transport: {transport}")

            session = await stack.enter_async_context(
                ClientSession(read_stream, write_stream)
            )
            init_result = await session.initialize()

            self._sessions[conn_id] = session
            self._init_results[conn_id] = init_result
            logger.info("MCP client session connected for %s via %s", conn_id, transport)
            return session

        except Exception:
            # Clean up on failure
            try:
                await stack.aclose()
            except Exception:
                pass
            self._stacks.pop(conn_id, None)
            raise

    async def disconnect(self, conn_id: str) -> None:
        """Close session + transport for a connection."""
        self._sessions.pop(conn_id, None)
        self._init_results.pop(conn_id, None)
        stack = self._stacks.pop(conn_id, None)
        if stack is not None:
            try:
                await stack.aclose()
            except Exception:
                logger.debug("Error closing MCP session for %s", conn_id, exc_info=True)
        logger.info("MCP client session disconnected for %s", conn_id)

    def get_session(self, conn_id: str) -> ClientSession | None:
        """Return the active session for a connection, or None."""
        return self._sessions.get(conn_id)

    def get_server_info(self, conn_id: str) -> tuple[str, str]:
        """Return ``(server_name, server_version)`` for a connected session.

        Reads from the cached ``InitializeResult``. Returns empty strings if
        the connection isn't active or the server didn't advertise itself.
        Tolerates both ``serverInfo`` and ``server_info`` attribute spellings
        across mcp-sdk versions.
        """
        init_result = self._init_results.get(conn_id)
        if init_result is None:
            return ("", "")

        server_info = (
            getattr(init_result, "serverInfo", None)
            or getattr(init_result, "server_info", None)
        )
        if server_info is None:
            return ("", "")

        name = getattr(server_info, "name", "") or ""
        version = getattr(server_info, "version", "") or ""
        return (name, version)

    async def call_tool(
        self,
        conn_id: str,
        tool_name: str,
        args: dict[str, Any],
        timeout: float = 30.0,
    ) -> dict[str, Any]:
        """Call a tool on an external MCP server.

        Returns a dict with ``success``, ``data``, and optional ``error``.
        """
        session = self._sessions.get(conn_id)
        if session is None:
            return {"success": False, "error": f"No active session for {conn_id}"}

        result = await asyncio.wait_for(
            session.call_tool(tool_name, args),
            timeout=timeout,
        )

        # Convert CallToolResult to a simple dict
        # result.content is a list of content blocks
        if result.isError:
            error_text = ""
            for block in result.content:
                if hasattr(block, "text"):
                    error_text += block.text
            return {"success": False, "error": error_text or "Tool call failed"}

        # Extract text content from result
        data: dict[str, Any] = {}
        texts: list[str] = []
        for block in result.content:
            if hasattr(block, "text"):
                texts.append(block.text)
        if texts:
            # Try to parse as JSON first
            import json
            combined = "\n".join(texts)
            try:
                data = json.loads(combined)
            except (json.JSONDecodeError, TypeError):
                data = {"text": combined}

        return {"success": True, "data": data}

    async def list_tools(self, conn_id: str) -> list[dict[str, Any]]:
        """Discover tools from an external MCP server.

        Returns a list of tool definitions.
        """
        session = self._sessions.get(conn_id)
        if session is None:
            return []

        result = await session.list_tools()
        tools = []
        for tool in result.tools:
            tool_def: dict[str, Any] = {
                "name": tool.name,
                "description": tool.description or "",
            }
            if tool.inputSchema:
                tool_def["input_schema"] = tool.inputSchema
            tools.append(tool_def)
        return tools

    async def disconnect_all(self) -> None:
        """Disconnect all active sessions."""
        conn_ids = list(self._sessions.keys())
        for conn_id in conn_ids:
            try:
                await self.disconnect(conn_id)
            except Exception:
                logger.debug("Error disconnecting %s", conn_id, exc_info=True)
