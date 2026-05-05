"""ConnectionManager — core lifecycle orchestrator for MCP external connections."""

from __future__ import annotations

import json
import logging
import re
from datetime import datetime, timezone
from typing import Any

from .models import McpConnection
from .credential import decrypt_credential
from .client_pool import McpClientPool

logger = logging.getLogger(__name__)

_NAME_PATTERN = re.compile(r"^[a-z0-9_]+$")


def _sanitize_name(name: str) -> str:
    """Sanitize a connection name to [a-z0-9_]+."""
    sanitized = re.sub(r"[^a-z0-9_]", "_", name.lower().strip())
    sanitized = re.sub(r"_+", "_", sanitized).strip("_")
    return sanitized or "unnamed"


class ConnectionManager:
    """Manages the full lifecycle of MCP external connections.

    Owns the McpClientPool, handles CRUD, tool discovery/registration.
    """

    def __init__(self, connection_store: Any, registry: Any) -> None:
        self._store = connection_store
        self._registry = registry
        self._pool = McpClientPool()
        self._connections: dict[str, McpConnection] = {}
        # Reverse map: tool_name → connection_id
        self._tool_to_connection: dict[str, str] = {}

    # ------------------------------------------------------------------
    # DB loading
    # ------------------------------------------------------------------

    def load_from_db(self) -> None:
        """Load all connections from the store on startup."""
        try:
            rows = self._store.load_connections()
        except Exception:
            logger.exception("Failed to load connections from store")
            return

        for row in rows:
            conn = McpConnection.from_dict(row)
            # Reset runtime status on load
            conn.status = "disconnected"
            self._connections[conn.id] = conn
        logger.info("Loaded %d connections from store", len(self._connections))

    # ------------------------------------------------------------------
    # CRUD
    # ------------------------------------------------------------------

    def add_connection(self, conn: McpConnection) -> McpConnection:
        """Add a new connection, validate name uniqueness, persist."""
        conn.name = _sanitize_name(conn.name)
        if not conn.name:
            raise ValueError("Connection name is required")

        # Check uniqueness
        for existing in self._connections.values():
            if existing.name == conn.name:
                raise ValueError(f"Connection name '{conn.name}' already exists")

        conn.created_at = datetime.now(timezone.utc).isoformat()
        conn.updated_at = conn.created_at
        self._connections[conn.id] = conn
        self._store.save_connection(conn.to_dict())
        logger.info("Added connection '%s' (id=%s)", conn.name, conn.id)
        return conn

    def remove_connection(self, conn_id: str) -> bool:
        """Disconnect, unregister tools, delete from DB."""
        conn = self._connections.pop(conn_id, None)
        if conn is None:
            return False

        # Unregister tools
        self._unregister_tools(conn)
        self._store.delete_connection(conn_id)
        logger.info("Removed connection '%s' (id=%s)", conn.name, conn_id)
        return True

    def update_connection(self, conn_id: str, updates: dict[str, Any]) -> McpConnection | None:
        """Update fields on a connection. Returns updated connection or None."""
        conn = self._connections.get(conn_id)
        if conn is None:
            return None

        # Apply updates to the in-memory object
        if "name" in updates:
            new_name = _sanitize_name(updates["name"])
            # Check uniqueness (excluding self)
            for existing in self._connections.values():
                if existing.id != conn_id and existing.name == new_name:
                    raise ValueError(f"Connection name '{new_name}' already exists")
            updates["name"] = new_name

        for key, value in updates.items():
            if hasattr(conn, key):
                setattr(conn, key, value)

        conn.updated_at = datetime.now(timezone.utc).isoformat()
        self._store.update_connection(conn_id, updates)
        return conn

    def get_connection(self, conn_id: str) -> McpConnection | None:
        """Get a connection by ID."""
        return self._connections.get(conn_id)

    def get_all_connections(self) -> list[McpConnection]:
        """Get all connections."""
        return list(self._connections.values())

    def get_connection_for_tool(self, tool_name: str) -> McpConnection | None:
        """Reverse lookup: find the connection that owns a tool."""
        conn_id = self._tool_to_connection.get(tool_name)
        if conn_id is None:
            return None
        return self._connections.get(conn_id)

    # ------------------------------------------------------------------
    # Enable / Disable
    # ------------------------------------------------------------------

    async def enable_connection(self, conn_id: str) -> McpConnection | None:
        """Enable a connection and connect."""
        conn = self._connections.get(conn_id)
        if conn is None:
            return None
        conn.enabled = True
        conn.updated_at = datetime.now(timezone.utc).isoformat()
        self._store.update_connection(conn_id, {"enabled": True})
        await self.connect(conn_id)
        return conn

    async def disable_connection(self, conn_id: str) -> McpConnection | None:
        """Disable a connection and disconnect."""
        conn = self._connections.get(conn_id)
        if conn is None:
            return None
        conn.enabled = False
        conn.updated_at = datetime.now(timezone.utc).isoformat()
        self._store.update_connection(conn_id, {"enabled": False})
        await self.disconnect(conn_id)
        return conn

    # ------------------------------------------------------------------
    # Connect / Disconnect
    # ------------------------------------------------------------------

    async def connect(self, conn_id: str) -> None:
        """Decrypt credential, establish session, discover + register tools."""
        conn = self._connections.get(conn_id)
        if conn is None:
            raise ValueError(f"Connection {conn_id} not found")

        conn.status = "connecting"
        conn.last_error = None
        self._update_status(conn)

        try:
            # Decrypt credential if needed
            credential = ""
            if conn.auth_credential_enc:
                credential = decrypt_credential(conn.auth_credential_enc)

            # Build headers with auth
            headers = dict(conn.headers)
            if credential:
                if conn.auth_type == "bearer":
                    headers["Authorization"] = f"Bearer {credential}"
                elif conn.auth_type == "api_key":
                    headers["X-API-Key"] = credential
                elif conn.auth_type == "custom_header":
                    # Credential is expected to be "HeaderName: value"
                    if ":" in credential:
                        key, val = credential.split(":", 1)
                        headers[key.strip()] = val.strip()

            # Connect via pool
            await self._pool.connect(
                conn_id,
                conn.transport,
                command=conn.command,
                args=conn.args,
                env=conn.env if conn.env else None,
                url=conn.url,
                headers=headers if headers else None,
            )

            # Cache server identity from the InitializeResult
            server_name, server_version = self._pool.get_server_info(conn_id)
            conn.server_name = server_name
            conn.server_version = server_version

            # Discover and register tools
            tools = await self._pool.list_tools(conn_id)
            conn.discovered_tools_json = json.dumps(tools, default=str)
            self._register_tools(conn, tools)

            conn.status = "connected"
            conn.last_connected_at = datetime.now(timezone.utc).isoformat()
            conn.last_error = None
            self._update_status(conn)
            logger.info(
                "Connected to '%s' (%s %s), discovered %d tools",
                conn.name, conn.server_name, conn.server_version, len(tools),
            )

        except Exception as e:
            conn.status = "error"
            conn.last_error = str(e)
            self._update_status(conn)
            logger.exception("Failed to connect to '%s'", conn.name)
            raise

    async def disconnect(self, conn_id: str) -> None:
        """Unregister tools, close session, update status."""
        conn = self._connections.get(conn_id)
        if conn is not None:
            self._unregister_tools(conn)
            conn.status = "disconnected"
            conn.last_error = None
            self._update_status(conn)

        try:
            await self._pool.disconnect(conn_id)
        except Exception:
            logger.debug("Error disconnecting %s", conn_id, exc_info=True)

    async def test_connection(self, conn_id: str) -> dict[str, Any]:
        """Temporarily connect, discover tools, disconnect. Returns results."""
        conn = self._connections.get(conn_id)
        if conn is None:
            return {"success": False, "error": "Connection not found"}

        # Save current state
        was_connected = conn.status == "connected"

        try:
            # If not already connected, do a temporary connect
            if not was_connected:
                await self.connect(conn_id)

            tools = await self._pool.list_tools(conn_id)
            result = {
                "success": True,
                "discovered_tools": tools,
                "server_name": conn.server_name,
                "server_version": conn.server_version,
            }

            # If it was a temporary connection and not enabled, disconnect
            if not was_connected and not conn.enabled:
                await self.disconnect(conn_id)

            return result

        except Exception as e:
            if not was_connected:
                try:
                    await self.disconnect(conn_id)
                except Exception:
                    pass
            return {"success": False, "error": str(e)}

    async def connect_all_enabled(self) -> None:
        """Connect all enabled connections. Non-fatal per-connection."""
        for conn in list(self._connections.values()):
            if conn.enabled:
                try:
                    await self.connect(conn.id)
                except Exception:
                    logger.warning(
                        "Auto-connect failed for '%s' (non-fatal)", conn.name
                    )

    async def disconnect_all(self) -> None:
        """Disconnect all active connections."""
        for conn_id in list(self._connections.keys()):
            try:
                await self.disconnect(conn_id)
            except Exception:
                logger.debug("Error disconnecting %s", conn_id, exc_info=True)
        await self._pool.disconnect_all()

    # ------------------------------------------------------------------
    # Tool registration
    # ------------------------------------------------------------------

    def _register_tools(self, conn: McpConnection, tools: list[dict[str, Any]]) -> None:
        """Register discovered tools in the ToolRegistry with namespaced names."""
        for tool in tools:
            original_name = tool.get("name", "")
            if not original_name:
                continue

            # Namespace: mcp.{connection_name}.{original_tool_name}
            safe_tool_name = re.sub(r"[^a-z0-9_]", "_", original_name.lower())
            namespaced = f"mcp.{conn.name}.{safe_tool_name}"

            # Convert MCP inputSchema to our parameters format
            input_schema = tool.get("input_schema", {})
            parameters = {
                "type": "object",
                "properties": input_schema.get("properties", {}),
                "required": input_schema.get("required", []),
            }

            definition: dict[str, Any] = {
                "name": namespaced,
                "adapter": "mcp",
                "description": tool.get("description", f"External MCP tool: {original_name}"),
                "parameters": parameters,
                "metadata": {
                    "connection_id": conn.id,
                    "connection_name": conn.name,
                    "original_tool_name": original_name,
                },
                "visibility": "user",
            }

            try:
                self._registry.register(definition)
                self._tool_to_connection[namespaced] = conn.id
                logger.debug("Registered external tool: %s", namespaced)
            except Exception:
                logger.warning("Failed to register tool %s", namespaced, exc_info=True)

    def _unregister_tools(self, conn: McpConnection) -> None:
        """Unregister all tools belonging to a connection."""
        prefix = f"mcp.{conn.name}."
        to_remove = [name for name in self._tool_to_connection if name.startswith(prefix)]
        for name in to_remove:
            try:
                self._registry.unregister(name)
            except Exception:
                logger.debug("Failed to unregister tool %s", name, exc_info=True)
            self._tool_to_connection.pop(name, None)

    # ------------------------------------------------------------------
    # Helpers
    # ------------------------------------------------------------------

    def _update_status(self, conn: McpConnection) -> None:
        """Persist status/error changes to the store."""
        try:
            self._store.update_connection(conn.id, {
                "status": conn.status,
                "last_error": conn.last_error,
                "server_name": conn.server_name,
                "server_version": conn.server_version,
                "discovered_tools_json": conn.discovered_tools_json,
                "last_connected_at": conn.last_connected_at,
            })
        except Exception:
            logger.debug("Failed to persist connection status", exc_info=True)
