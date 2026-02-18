"""Routes validated tool calls to the appropriate adapter."""

from __future__ import annotations

import importlib
import json
import logging
import time
from pathlib import Path
from typing import Any

from ..registry.registry import ToolRegistry
from ..registry.schema_validator import (
    validate_tool_args_detailed,
    get_tool_permissions,
    get_tool_side_effects,
)
from .result import ToolResult

logger = logging.getLogger(__name__)


class Dispatcher:
    """Routes tool calls to their adapter based on the tool definition.

    Supports optional safety features: precondition checks, dry-run mode,
    and approval gates.
    """

    def __init__(
        self,
        registry: ToolRegistry,
        adapters: dict[str, Any],
        handlers_dir: Path,
        precondition_checker: Any | None = None,
        approval_gate: Any | None = None,
    ) -> None:
        self._registry = registry
        self._adapters = adapters
        self._handlers_dir = handlers_dir
        self._handler_cache: dict[str, Any] = {}
        self._precondition_checker = precondition_checker
        self._approval_gate = approval_gate

    async def dispatch(
        self,
        tool_name: str,
        args: dict[str, Any],
        dry_run: bool = False,
        execution_mode: str = "headless",
        definition: dict[str, Any] | None = None,
    ) -> ToolResult:
        """Dispatch a tool call to the appropriate adapter/handler.

        Steps:
        1. Look up the tool definition in the registry
        2. Coerce string-encoded arrays/objects to native types
        3. Validate args against the tool's parameter schema
        4. Check preconditions (if checker configured)
        5. Dry-run shortcut (if requested)
        6. Approval gate (if tool requires it)
        7. Determine adapter
        8. Load the handler module and execute
        """
        start = time.perf_counter_ns()

        # 1. Look up tool (use inline definition if provided)
        if definition is None:
            definition = self._registry.get(tool_name)
            if definition is None:
                return ToolResult.fail(
                    "TOOL_NOT_FOUND", f"No tool registered: {tool_name}",
                    stage="preflight_validation",
                )

        # 1b. Coerce None/null args to empty dict — callers may pass None when
        # no arguments are supplied (e.g. Dynamo tools with zero inputs).
        if args is None:
            args = {}

        # 2. Coerce string-encoded arrays/objects to native types
        args = _coerce_args(args, definition["parameters"])

        # 3. Validate args
        validation_errors = validate_tool_args_detailed(args, definition["parameters"])
        if validation_errors:
            messages = [e["message"] for e in validation_errors]
            result = ToolResult.fail(
                "SCHEMA_VALIDATION_FAILED",
                f"Argument validation failed: {'; '.join(messages)}",
                stage="preflight_validation",
            )
            result.data = {"validation_errors": validation_errors}
            return result

        # 4. Check preconditions
        if self._precondition_checker is not None:
            precond_failures = await self._precondition_checker.check(definition)
            if precond_failures:
                return ToolResult.fail(
                    "PRECONDITION_FAILED",
                    f"Preconditions not met: {'; '.join(precond_failures)}",
                    stage="preflight_validation",
                )

        # 5. Dry-run shortcut
        if dry_run:
            permissions = get_tool_permissions(definition)
            side_effects = get_tool_side_effects(definition)
            return ToolResult.ok({
                "dry_run": True,
                "tool_name": tool_name,
                "permissions": permissions,
                "side_effects": side_effects,
                "precondition_results": "passed",
                "args": args,
            })

        # 6. Approval gate
        if self._approval_gate is not None:
            if self._approval_gate.needs_approval(definition):
                side_effects = get_tool_side_effects(definition)
                approved = await self._approval_gate.request_approval(
                    tool_name, args, side_effects
                )
                if not approved:
                    return ToolResult.fail(
                        "USER_DENIED",
                        f"User denied execution of {tool_name}",
                        stage="preflight_validation",
                    )

        # 7. Determine adapter
        adapter_name = definition["adapter"]
        adapter = self._adapters.get(adapter_name)
        if adapter is None:
            return ToolResult.fail(
                "ADAPTER_NOT_AVAILABLE",
                f"Adapter '{adapter_name}' is not available",
                stage="dispatch",
            )

        # 8. Load handler and execute
        try:
            handler = self._load_handler(tool_name)
            # Pass execution_mode and definition to adapter; fall back gracefully
            # if the adapter doesn't accept the newer kwargs.
            try:
                result = await adapter.execute(
                    tool_name, args, handler,
                    execution_mode=execution_mode, definition=definition,
                )
            except TypeError:
                try:
                    result = await adapter.execute(
                        tool_name, args, handler, execution_mode=execution_mode
                    )
                except TypeError:
                    result = await adapter.execute(tool_name, args, handler)
            elapsed_ms = int((time.perf_counter_ns() - start) / 1_000_000)
            result.duration_ms = elapsed_ms
            if not result.stage:
                result.stage = "adapter_execution"
            return result
        except Exception as e:
            elapsed_ms = int((time.perf_counter_ns() - start) / 1_000_000)
            logger.exception("Handler error for tool %s", tool_name)
            return ToolResult.fail(
                "HANDLER_ERROR", str(e),
                duration_ms=elapsed_ms, stage="adapter_execution",
            )

    def _load_handler(self, tool_name: str) -> Any:
        """Load the handler module for a tool, or return None if not found.

        Handler modules are optional.  Adapters like RevitAddinAdapter ignore
        them entirely, and WorkflowAdapter uses the declarative engine path
        when no handler is present.
        """
        if tool_name in self._handler_cache:
            return self._handler_cache[tool_name]

        module_name = tool_name.replace(".", "_")
        module_path = f"orchestrator.handlers.{module_name}"

        try:
            module = importlib.import_module(module_path)
        except ModuleNotFoundError:
            logger.debug(
                "No handler module for %s (checked %s) — adapter will handle directly",
                tool_name,
                module_path,
            )
            self._handler_cache[tool_name] = None
            return None

        if not hasattr(module, "execute"):
            raise AttributeError(
                f"Handler module {module_path} must define an 'execute' function"
            )

        self._handler_cache[tool_name] = module
        return module


def _coerce_args(
    args: dict[str, Any] | None, parameters_schema: dict[str, Any]
) -> dict[str, Any]:
    """Coerce argument values to match the schema's expected types.

    Handles two cases:
    1. **Null → empty container**: If a property is declared as ``type: "object"``
       or ``type: "array"`` and the caller sends ``None``/``null``, coerce to
       ``{}`` or ``[]`` respectively.  This is the common case for Dynamo tools
       with zero inputs.
    2. **String → parsed JSON**: Workflow recordings previously serialised arrays
       as strings (e.g. ``"[1,2,3]"`` instead of ``[1,2,3]``).  Parse them back
       before schema validation so existing saved workflows continue to work.
    """
    if args is None:
        return {}

    properties = parameters_schema.get("properties", {})
    if not properties:
        return args

    coerced = dict(args)
    for key, value in coerced.items():
        if key not in properties:
            continue
        expected = properties[key].get("type")

        # Coerce null/None to empty container for object/array properties
        if value is None:
            if expected == "object":
                coerced[key] = {}
            elif expected == "array":
                coerced[key] = []
            continue

        # Coerce string-encoded JSON to native types
        if isinstance(value, str) and expected in ("array", "object") and value.lstrip().startswith(("[", "{")):
            try:
                parsed = json.loads(value)
                if (expected == "array" and isinstance(parsed, list)) or (
                    expected == "object" and isinstance(parsed, dict)
                ):
                    coerced[key] = parsed
            except (json.JSONDecodeError, ValueError):
                pass  # Leave as-is; schema validation will catch it
    return coerced
