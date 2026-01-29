"""Abstract adapter interface."""

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import Any

from ..dispatcher.result import ToolResult


class BaseAdapter(ABC):
    """Base class for all execution adapters."""

    @property
    @abstractmethod
    def name(self) -> str:
        """Return the adapter identifier (e.g., 'revit', 'pyrevit')."""
        ...

    @abstractmethod
    async def execute(
        self, tool_name: str, args: dict[str, Any], handler: Any
    ) -> ToolResult:
        """Execute a tool call.

        Args:
            tool_name: The fully qualified tool name.
            args: Validated arguments dict.
            handler: The loaded handler module (has an `execute` function).

        Returns:
            ToolResult with success/failure and data.
        """
        ...

    @abstractmethod
    async def is_available(self) -> bool:
        """Check if this adapter is currently available."""
        ...
