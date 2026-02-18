"""Workflow execution engine for declarative step-based workflows."""

from __future__ import annotations

import asyncio
import logging
import time
from typing import Any

from ..dispatcher.result import ToolResult
from .context import WorkflowContext
from .result import WorkflowResult

logger = logging.getLogger(__name__)


class WorkflowEngine:
    """Executes declarative workflows defined as sequences of tool calls.

    Supports parameter bindings between steps, guard conditions,
    per-step failure handling (stop/skip/retry), timeouts, and
    optional reconciliation for type mismatches during replay.
    """

    def __init__(
        self,
        dispatcher: Any,
        audit_log: Any | None = None,
        execution_logger: Any | None = None,
        reconciler: Any | None = None,
    ) -> None:
        self._dispatcher = dispatcher
        self._audit_log = audit_log
        self._execution_logger = execution_logger
        self._reconciler = reconciler

    async def execute(
        self,
        workflow_def: dict[str, Any],
        input_args: dict[str, Any] | None = None,
    ) -> WorkflowResult:
        """Execute a workflow definition.

        Args:
            workflow_def: The workflow definition with a "steps" array.
            input_args: Optional input arguments for the workflow.

        Returns:
            WorkflowResult with per-step results and aggregated changes.
        """
        steps = workflow_def.get("steps", [])
        context = WorkflowContext(input_args)
        start_time = time.perf_counter_ns()

        steps_completed = 0
        overall_success = True

        for i, step in enumerate(steps):
            step_id = step.get("id", f"step{i + 1}")
            tool_name = step.get("tool", "")
            static_args = dict(step.get("args", {}))
            bindings = step.get("bindings", {})
            guard = step.get("guard")
            on_failure = step.get("on_failure", "stop")
            max_retries = step.get("max_retries", 0)
            timeout_ms = step.get("timeout_ms")
            snapshot = step.get("snapshot")  # None for legacy workflows

            # Evaluate guard
            if guard:
                try:
                    guard_value = context.evaluate(guard)
                    if not guard_value:
                        logger.debug("Step %s: guard '%s' is falsy, skipping", step_id, guard)
                        context.skipped_steps.append(step_id)
                        continue
                except Exception as e:
                    logger.warning("Step %s: guard evaluation error: %s", step_id, e)
                    context.skipped_steps.append(step_id)
                    continue

            # Resolve bindings
            try:
                bound_args = context.resolve_bindings(bindings) if bindings else {}
            except (KeyError, ValueError) as e:
                logger.error("Step %s: binding resolution failed: %s", step_id, e)
                result = ToolResult.fail("BINDING_ERROR", str(e))
                context.record_result(step_id, result)
                if on_failure == "stop":
                    overall_success = False
                    break
                elif on_failure == "skip":
                    context.skipped_steps.append(step_id)
                    continue
                continue

            # Merge static args with bound args (bindings override static).
            # Skip None values so that unresolved $input.xxx bindings
            # fall back to the static default instead of wiping it out.
            final_args = {**static_args}
            for k, v in bound_args.items():
                if v is not None:
                    final_args[k] = v

            # Reconcile step args if reconciler is available and tool needs it
            _RECONCILABLE_TOOLS = {"revit.create_element", "revit.place_family"}
            if self._reconciler and tool_name in _RECONCILABLE_TOOLS:
                try:
                    reconciled_args = await self._reconciler.reconcile_step(
                        {"tool": tool_name, "args": final_args}
                    )
                    if reconciled_args is None:
                        # User cancelled mapping
                        result = ToolResult.fail(
                            "USER_CANCELLED_MAPPING",
                            f"User cancelled type reconciliation for step '{step_id}'",
                        )
                        context.record_result(step_id, result)
                        if on_failure == "stop":
                            overall_success = False
                            break
                        elif on_failure == "skip":
                            context.skipped_steps.append(step_id)
                            continue
                        continue
                    final_args = reconciled_args
                except Exception as e:
                    logger.warning(
                        "Step %s: reconciliation error (proceeding with original args): %s",
                        step_id, e,
                    )

            # Log step start if execution logger is available
            event_id: str | None = None
            if self._execution_logger:
                event_id = await self._execution_logger.log_started(tool_name, final_args)

            # Execute with retries
            result = await self._execute_step(
                step_id, tool_name, final_args, timeout_ms, max_retries,
                snapshot=snapshot,
            )

            # Log step completion/failure
            if self._execution_logger and event_id:
                if result.success:
                    await self._execution_logger.log_completed(event_id, result, tool_name, final_args)
                else:
                    await self._execution_logger.log_failed(
                        event_id, result.error_message or "Step failed", tool_name, final_args
                    )

            context.record_result(step_id, result)
            steps_completed += 1

            if not result.success:
                if on_failure == "stop":
                    overall_success = False
                    break
                elif on_failure == "skip":
                    context.skipped_steps.append(step_id)
                    continue
                # "retry" is handled inside _execute_step

        total_duration_ms = int((time.perf_counter_ns() - start_time) / 1_000_000)

        wf_result = WorkflowResult(
            success=overall_success,
            steps_completed=steps_completed,
            steps_total=len(steps),
            step_results=context.step_results,
            skipped_steps=context.skipped_steps,
            aggregated_model_changes=context.get_aggregated_changes(),
            total_duration_ms=total_duration_ms,
        )

        # Write workflow_completed event to audit log
        if self._audit_log is not None:
            audit_event: dict[str, Any] = {
                "event_type": "workflow_completed",
                "success": overall_success,
                "steps_completed": steps_completed,
                "steps_total": len(steps),
                "total_duration_ms": total_duration_ms,
                "skipped_steps": context.skipped_steps,
            }

            # Include governance metadata if present
            governance = workflow_def.get("governance", {})
            if governance:
                audit_event["governance"] = {
                    "author": governance.get("author", ""),
                    "version": governance.get("version", ""),
                    "permission_mode": governance.get("permission_mode", ""),
                    "approval_required": governance.get("approval_required", False),
                    "tags": governance.get("tags", []),
                    "side_effects": governance.get("side_effects", []),
                }

            self._audit_log.log_event(audit_event)

        return wf_result

    async def _execute_step(
        self,
        step_id: str,
        tool_name: str,
        args: dict[str, Any],
        timeout_ms: int | None,
        max_retries: int,
        snapshot: dict[str, Any] | None = None,
    ) -> ToolResult:
        """Execute a single step with optional timeout and retries."""
        attempts = 0
        last_result: ToolResult | None = None

        while attempts <= max_retries:
            attempts += 1
            try:
                if timeout_ms:
                    timeout_s = timeout_ms / 1000.0
                    last_result = await asyncio.wait_for(
                        self._dispatcher.dispatch(
                            tool_name, args, definition=snapshot
                        ),
                        timeout=timeout_s,
                    )
                else:
                    last_result = await self._dispatcher.dispatch(
                        tool_name, args, definition=snapshot
                    )

                if last_result.success:
                    return last_result

                # Failed - retry if we have attempts left
                if attempts <= max_retries:
                    logger.warning(
                        "Step %s attempt %d failed, retrying (%d left)",
                        step_id, attempts, max_retries - attempts + 1,
                    )
                    continue

            except asyncio.TimeoutError:
                last_result = ToolResult.fail(
                    "STEP_TIMEOUT",
                    f"Step '{step_id}' timed out after {timeout_ms}ms",
                )
                if attempts <= max_retries:
                    continue
            except Exception as e:
                last_result = ToolResult.fail("STEP_ERROR", str(e))
                if attempts <= max_retries:
                    continue

        return last_result or ToolResult.fail("STEP_ERROR", "No result")
