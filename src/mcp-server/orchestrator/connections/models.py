"""Data model for MCP external connections."""

from __future__ import annotations

import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any


@dataclass
class McpConnection:
    """Represents a managed connection to an external MCP server."""

    id: str = field(default_factory=lambda: str(uuid.uuid4()))
    name: str = ""
    transport: str = "stdio"  # "stdio" | "sse" | "streamable_http"
    enabled: bool = True

    # stdio fields
    command: str = ""
    args: list[str] = field(default_factory=list)
    env: dict[str, str] = field(default_factory=dict)

    # sse / streamable_http fields
    url: str = ""
    headers: dict[str, str] = field(default_factory=dict)

    # auth
    auth_type: str = "none"  # "none" | "api_key" | "bearer" | "custom_header"
    auth_credential_enc: str = ""  # DPAPI-encrypted, base64-encoded

    # server info (cached from last connect)
    server_name: str = ""
    server_version: str = ""
    discovered_tools_json: str = "[]"

    # runtime state
    status: str = "disconnected"  # "disconnected" | "connecting" | "connected" | "error"
    last_error: str | None = None

    # timestamps
    created_at: str = field(default_factory=lambda: datetime.now(timezone.utc).isoformat())
    updated_at: str = field(default_factory=lambda: datetime.now(timezone.utc).isoformat())
    last_connected_at: str | None = None

    def to_dict(self) -> dict[str, Any]:
        """Serialize to dict (includes encrypted credential for persistence)."""
        return {
            "id": self.id,
            "name": self.name,
            "transport": self.transport,
            "enabled": self.enabled,
            "command": self.command,
            "args": self.args,
            "env": self.env,
            "url": self.url,
            "headers": self.headers,
            "auth_type": self.auth_type,
            "auth_credential_enc": self.auth_credential_enc,
            "server_name": self.server_name,
            "server_version": self.server_version,
            "discovered_tools_json": self.discovered_tools_json,
            "status": self.status,
            "last_error": self.last_error,
            "created_at": self.created_at,
            "updated_at": self.updated_at,
            "last_connected_at": self.last_connected_at,
        }

    def to_summary_dict(self) -> dict[str, Any]:
        """Serialize for UI display — omits credential."""
        import json

        try:
            tools = json.loads(self.discovered_tools_json)
        except (json.JSONDecodeError, TypeError):
            tools = []

        return {
            "id": self.id,
            "name": self.name,
            "transport": self.transport,
            "enabled": self.enabled,
            "command": self.command,
            "args": self.args,
            "url": self.url,
            "auth_type": self.auth_type,
            "server_name": self.server_name,
            "server_version": self.server_version,
            "discovered_tool_count": len(tools),
            "discovered_tool_names": [t.get("name", "") for t in tools] if isinstance(tools, list) else [],
            "status": self.status,
            "last_error": self.last_error,
            "created_at": self.created_at,
            "updated_at": self.updated_at,
            "last_connected_at": self.last_connected_at,
        }

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> McpConnection:
        """Deserialize from dict."""
        return cls(
            id=data.get("id", str(uuid.uuid4())),
            name=data.get("name", ""),
            transport=data.get("transport", "stdio"),
            enabled=bool(data.get("enabled", True)),
            command=data.get("command", ""),
            args=data.get("args", []),
            env=data.get("env", {}),
            url=data.get("url", ""),
            headers=data.get("headers", {}),
            auth_type=data.get("auth_type", "none"),
            auth_credential_enc=data.get("auth_credential_enc", ""),
            server_name=data.get("server_name", ""),
            server_version=data.get("server_version", ""),
            discovered_tools_json=data.get("discovered_tools_json", "[]"),
            status=data.get("status", "disconnected"),
            last_error=data.get("last_error"),
            created_at=data.get("created_at", datetime.now(timezone.utc).isoformat()),
            updated_at=data.get("updated_at", datetime.now(timezone.utc).isoformat()),
            last_connected_at=data.get("last_connected_at"),
        )
