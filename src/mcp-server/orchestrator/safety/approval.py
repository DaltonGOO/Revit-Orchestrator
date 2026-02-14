"""Approval gate for write operations requiring user confirmation."""

from __future__ import annotations

import logging
from typing import Any

logger = logging.getLogger(__name__)


class ApprovalGate:
    """Handles user approval for sensitive tool executions.

    When a tool has `permissions.approval_required: true`, this gate
    sends an approval request to the C# UI and waits for a response.
    """

    def __init__(self, connection: Any | None = None) -> None:
        self._connection = connection

    def set_connection(self, connection: Any) -> None:
        self._connection = connection

    def needs_approval(self, definition: dict[str, Any]) -> bool:
        """Check if a tool requires user approval."""
        permissions = definition.get("permissions", {})
        return permissions.get("approval_required", False)

    async def request_approval(
        self,
        tool_name: str,
        args: dict[str, Any],
        side_effects: list[str],
    ) -> bool:
        """Request approval from the user via the C# UI.

        Returns True if approved, False if denied.
        """
        if self._connection is None:
            logger.warning("No connection for approval request, auto-approving")
            return True

        from ..pipe.protocol import make_approval_request

        try:
            response = await self._connection.send_and_wait(
                make_approval_request(tool_name, args, side_effects),
                timeout=120.0,
            )
            if response and response.get("type") == "approval_response":
                return response.get("payload", {}).get("approved", False)
        except Exception:
            logger.exception("Error requesting approval for %s", tool_name)

        return False
