"""Precondition checking before tool execution."""

from __future__ import annotations

import logging
from typing import Any

logger = logging.getLogger(__name__)


class PreconditionChecker:
    """Checks tool preconditions against the current Revit state.

    Sends a status_query to the C# add-in to get the current state,
    then evaluates preconditions from the tool definition.
    """

    def __init__(self, connection: Any | None = None) -> None:
        self._connection = connection
        self._cached_status: dict[str, Any] | None = None

    def set_connection(self, connection: Any) -> None:
        self._connection = connection

    async def get_status(self) -> dict[str, Any]:
        """Query the C# add-in for current Revit status."""
        if self._connection is None:
            return {}

        from ..pipe.protocol import make_status_query

        try:
            response = await self._connection.send_and_wait(
                make_status_query(), timeout=10.0
            )
            if response and response.get("type") == "status_response":
                self._cached_status = response.get("payload", {})
                return self._cached_status
        except Exception:
            logger.warning("Failed to get Revit status, using empty state")

        return self._cached_status or {}

    async def check(self, definition: dict[str, Any]) -> list[str]:
        """Check all preconditions for a tool definition.

        Returns a list of failed precondition messages (empty if all pass).
        """
        preconditions = definition.get("preconditions", [])
        if not preconditions:
            return []

        status = await self.get_status()
        failures: list[str] = []

        for precond in preconditions:
            check_type = precond.get("check", "")
            value = precond.get("value")

            if check_type == "document_open":
                if not status.get("document_title"):
                    failures.append("No Revit document is open")

            elif check_type == "revit_version_gte":
                if value:
                    current = status.get("revit_version", "0")
                    if str(current) < str(value):
                        failures.append(
                            f"Revit version {current} < required {value}"
                        )

            elif check_type == "worksharing_enabled":
                if not status.get("is_worksharing"):
                    failures.append("Worksharing is not enabled")

            else:
                logger.warning("Unknown precondition check: %s", check_type)

        return failures
