"""Tool result dataclass."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any


@dataclass
class ToolResult:
    """Result of a tool execution."""

    success: bool
    data: dict[str, Any] = field(default_factory=dict)
    error_code: str | None = None
    error_message: str | None = None
    duration_ms: int = 0

    def to_dict(self) -> dict[str, Any]:
        """Convert to a dict suitable for the pipe protocol payload."""
        result: dict[str, Any] = {
            "success": self.success,
            "data": self.data,
            "error": None,
            "duration_ms": self.duration_ms,
        }
        if not self.success and self.error_code:
            result["error"] = {
                "code": self.error_code,
                "message": self.error_message or "",
            }
        return result

    @classmethod
    def ok(cls, data: dict[str, Any], duration_ms: int = 0) -> ToolResult:
        """Create a successful result."""
        return cls(success=True, data=data, duration_ms=duration_ms)

    @classmethod
    def fail(
        cls, code: str, message: str, duration_ms: int = 0
    ) -> ToolResult:
        """Create a failed result."""
        return cls(
            success=False,
            error_code=code,
            error_message=message,
            duration_ms=duration_ms,
        )
