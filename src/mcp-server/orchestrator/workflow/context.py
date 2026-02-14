"""Workflow execution context with expression evaluation."""

from __future__ import annotations

import logging
import re
from typing import Any

from ..dispatcher.result import ToolResult

logger = logging.getLogger(__name__)


class WorkflowContext:
    """Holds state for a workflow execution: inputs, step results, skips."""

    def __init__(self, input_args: dict[str, Any] | None = None) -> None:
        self.input_args: dict[str, Any] = input_args or {}
        self.step_results: dict[str, ToolResult] = {}
        self.skipped_steps: list[str] = []
        self.all_created: list[dict[str, Any]] = []
        self.all_modified: list[dict[str, Any]] = []
        self.all_deleted: list[int] = []

    def record_result(self, step_id: str, result: ToolResult) -> None:
        """Record a step result and aggregate model changes."""
        self.step_results[step_id] = result
        if result.success and result.data:
            changes = result.data.get("model_changes")
            if changes:
                self.all_created.extend(changes.get("created", []))
                self.all_modified.extend(changes.get("modified", []))
                self.all_deleted.extend(changes.get("deleted", []))

    def get_aggregated_changes(self) -> dict[str, Any] | None:
        """Get all aggregated model changes, or None if none."""
        if not self.all_created and not self.all_modified and not self.all_deleted:
            return None
        return {
            "created": self.all_created,
            "modified": self.all_modified,
            "deleted": self.all_deleted,
        }

    def evaluate(self, expression: str) -> Any:
        """Evaluate a workflow expression.

        Supported expressions:
        - $input.key - resolves to input_args["key"]
        - $steps.step_id.success - resolves to step result success boolean
        - $steps.step_id.data.field - resolves to step result data["field"]
        - $steps.step_id.data.field.subfield - nested access

        Returns the resolved value or None if the path doesn't exist.
        """
        if not expression.startswith("$"):
            # Literal value
            return expression

        parts = expression[1:].split(".")

        if parts[0] == "input":
            return self._resolve_path(self.input_args, parts[1:])
        elif parts[0] == "steps":
            if len(parts) < 2:
                raise ValueError(f"Invalid expression: {expression}")
            step_id = parts[1]
            if step_id not in self.step_results:
                raise KeyError(
                    f"Step '{step_id}' not found in results. "
                    f"Available steps: {list(self.step_results.keys())}"
                )
            result = self.step_results[step_id]
            remaining = parts[2:]
            if not remaining:
                return result
            if remaining[0] == "success":
                return result.success
            if remaining[0] == "data":
                return self._resolve_path(result.data, remaining[1:])
            if remaining[0] == "error_code":
                return result.error_code
            raise ValueError(f"Unknown step attribute: {remaining[0]}")
        else:
            raise ValueError(f"Unknown expression root: {parts[0]}")

    def resolve_bindings(self, bindings: dict[str, str]) -> dict[str, Any]:
        """Resolve a dict of parameter bindings to concrete values."""
        resolved = {}
        for key, expr in bindings.items():
            resolved[key] = self.evaluate(expr)
        return resolved

    @staticmethod
    def _resolve_path(obj: Any, parts: list[str]) -> Any:
        """Walk a dotted path into a nested dict/object."""
        current = obj
        for part in parts:
            if isinstance(current, dict):
                if part not in current:
                    return None
                current = current[part]
            elif hasattr(current, part):
                current = getattr(current, part)
            else:
                return None
        return current
