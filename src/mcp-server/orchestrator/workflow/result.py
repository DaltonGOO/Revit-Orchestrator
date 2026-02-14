"""Workflow execution result."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

from ..dispatcher.result import ToolResult


@dataclass
class WorkflowResult:
    """Result of executing a complete workflow."""

    success: bool
    steps_completed: int = 0
    steps_total: int = 0
    step_results: dict[str, ToolResult] = field(default_factory=dict)
    skipped_steps: list[str] = field(default_factory=list)
    aggregated_model_changes: dict[str, Any] | None = None
    total_duration_ms: int = 0

    def to_tool_result(self) -> ToolResult:
        """Convert to a ToolResult for the dispatcher."""
        data: dict[str, Any] = {
            "workflow": True,
            "steps_completed": self.steps_completed,
            "steps_total": self.steps_total,
            "skipped_steps": self.skipped_steps,
            "step_summaries": {
                step_id: {
                    "success": r.success,
                    "error_code": r.error_code,
                    "duration_ms": r.duration_ms,
                }
                for step_id, r in self.step_results.items()
            },
        }
        if self.aggregated_model_changes:
            data["model_changes"] = self.aggregated_model_changes

        if self.success:
            return ToolResult.ok(data, duration_ms=self.total_duration_ms)
        else:
            # Find the first failed step for the error message
            for step_id, r in self.step_results.items():
                if not r.success:
                    return ToolResult.fail(
                        r.error_code or "WORKFLOW_STEP_FAILED",
                        f"Step '{step_id}' failed: {r.error_message}",
                        duration_ms=self.total_duration_ms,
                    )
            return ToolResult.fail(
                "WORKFLOW_FAILED",
                "Workflow failed",
                duration_ms=self.total_duration_ms,
            )
