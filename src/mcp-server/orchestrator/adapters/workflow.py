"""Adapter that executes composed multi-step workflow tools."""

from __future__ import annotations

import logging
from typing import Any

from .base import BaseAdapter
from ..dispatcher.result import ToolResult

logger = logging.getLogger(__name__)


class TrackingDispatcher:
    """Wrapper around dispatcher that tracks model changes from all sub-tool calls."""

    def __init__(self, dispatcher: Any) -> None:
        self._dispatcher = dispatcher
        self.all_created: list[dict[str, Any]] = []
        self.all_modified: list[dict[str, Any]] = []
        self.all_deleted: list[int] = []
        self.tool_results: list[ToolResult] = []

    async def dispatch(self, tool_name: str, args: dict[str, Any],
                       definition: dict[str, Any] | None = None) -> ToolResult:
        """Dispatch a tool call and track its model changes."""
        result = await self._dispatcher.dispatch(tool_name, args, definition=definition)
        self.tool_results.append(result)

        # Extract and aggregate model_changes from the result
        if result.success and result.data:
            changes = result.data.get("model_changes")
            if changes:
                self.all_created.extend(changes.get("created", []))
                self.all_modified.extend(changes.get("modified", []))
                self.all_deleted.extend(changes.get("deleted", []))

        return result

    def get_aggregated_changes(self) -> dict[str, Any] | None:
        """Get all aggregated model changes, or None if no changes."""
        if not self.all_created and not self.all_modified and not self.all_deleted:
            return None

        return {
            "created": self.all_created,
            "modified": self.all_modified,
            "deleted": self.all_deleted,
        }


class WorkflowAdapter(BaseAdapter):
    """Executes multi-step workflow tools.

    If the tool definition has a "workflow.steps" key, uses the declarative
    WorkflowEngine. Otherwise falls back to the handler-based execution.
    """

    def __init__(self, registry: Any | None = None) -> None:
        self._dispatcher: Any | None = None
        self._registry = registry
        self._audit_log: Any | None = None
        self._pipe_connection: Any | None = None

    @property
    def name(self) -> str:
        return "workflow"

    def set_dispatcher(self, dispatcher: Any) -> None:
        """Set the dispatcher for sub-tool calls within workflows."""
        self._dispatcher = dispatcher

    def set_registry(self, registry: Any) -> None:
        """Set the registry for looking up workflow definitions."""
        self._registry = registry

    def set_audit_log(self, audit_log: Any) -> None:
        """Set the audit log for workflow event tracking."""
        self._audit_log = audit_log

    def set_pipe_connection(self, pipe_connection: Any) -> None:
        """Set the pipe connection for reconciliation UI prompts."""
        self._pipe_connection = pipe_connection

    async def execute(
        self, tool_name: str, args: dict[str, Any], handler: Any,
        execution_mode: str = "headless",
        definition: dict[str, Any] | None = None,
    ) -> ToolResult:
        """Execute a workflow tool.

        If the tool definition has a 'workflow' key with 'steps', uses the
        declarative WorkflowEngine. Otherwise delegates to the handler module.
        """
        if self._dispatcher is None:
            return ToolResult.fail(
                "ADAPTER_NOT_AVAILABLE",
                "Workflow dispatcher not configured",
            )

        # Use provided definition (snapshot) if it has workflow steps
        if definition and "workflow" in definition:
            workflow_def = definition["workflow"]
            if "steps" in workflow_def:
                return await self._execute_declarative(
                    tool_name, args, workflow_def
                )

        # Check if this tool has a declarative workflow definition in the registry
        if self._registry is not None:
            reg_def = self._registry.get(tool_name)
            if reg_def and "workflow" in reg_def:
                workflow_def = reg_def["workflow"]
                if "steps" in workflow_def:
                    return await self._execute_declarative(
                        tool_name, args, workflow_def
                    )

        # Fall back to handler-based execution (requires a handler module)
        if handler is None:
            return ToolResult.fail(
                "HANDLER_NOT_FOUND",
                f"Workflow tool '{tool_name}' has no declarative definition and "
                f"no handler module. Either add a 'workflow.steps' section to "
                f"the tool JSON or create a handler module.",
            )
        return await self._execute_handler(tool_name, args, handler)

    async def _execute_declarative(
        self, tool_name: str, args: dict[str, Any], workflow_def: dict[str, Any]
    ) -> ToolResult:
        """Execute a declarative workflow using the WorkflowEngine."""
        try:
            from ..workflow.engine import WorkflowEngine
            from ..reconciliation.mapper import ReconciliationMapper

            # Construct reconciler if pipe connection is available
            reconciler = None
            if self._dispatcher and self._pipe_connection:
                reconciler = ReconciliationMapper(self._dispatcher, self._pipe_connection)

            engine = WorkflowEngine(
                self._dispatcher,
                audit_log=self._audit_log,
                reconciler=reconciler,
            )
            wf_result = await engine.execute(workflow_def, input_args=args)
            return wf_result.to_tool_result()
        except Exception as e:
            logger.exception("Declarative workflow error for %s", tool_name)
            return ToolResult.fail("WORKFLOW_ERROR", str(e))

    async def _execute_handler(
        self, tool_name: str, args: dict[str, Any], handler: Any
    ) -> ToolResult:
        """Execute a handler-based workflow."""
        try:
            tracking_dispatcher = TrackingDispatcher(self._dispatcher)
            result = await handler.execute(args, dispatcher=tracking_dispatcher)

            if result.success:
                aggregated_changes = tracking_dispatcher.get_aggregated_changes()
                if aggregated_changes:
                    new_data = dict(result.data) if result.data else {}
                    new_data["model_changes"] = aggregated_changes
                    result = ToolResult.ok(new_data, duration_ms=result.duration_ms)

            return result
        except Exception as e:
            logger.exception("Workflow error for %s", tool_name)
            return ToolResult.fail("HANDLER_ERROR", str(e))

    async def is_available(self) -> bool:
        return self._dispatcher is not None
