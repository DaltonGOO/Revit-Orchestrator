"""Routes validated tool calls to the appropriate adapter."""

from __future__ import annotations

import importlib
import logging
import time
from pathlib import Path
from typing import Any

from ..registry.registry import ToolRegistry
from ..registry.schema_validator import validate_tool_args
from .result import ToolResult

logger = logging.getLogger(__name__)


class Dispatcher:
    """Routes tool calls to their adapter based on the tool definition."""

    def __init__(
        self,
        registry: ToolRegistry,
        adapters: dict[str, Any],
        handlers_dir: Path,
    ) -> None:
        self._registry = registry
        self._adapters = adapters
        self._handlers_dir = handlers_dir
        self._handler_cache: dict[str, Any] = {}

    async def dispatch(self, tool_name: str, args: dict[str, Any]) -> ToolResult:
        """Dispatch a tool call to the appropriate adapter/handler.

        Steps:
        1. Look up the tool definition in the registry
        2. Validate args against the tool's parameter schema
        3. Load the handler module
        4. Execute via the appropriate adapter
        """
        start = time.perf_counter_ns()

        # 1. Look up tool
        definition = self._registry.get(tool_name)
        if definition is None:
            return ToolResult.fail("TOOL_NOT_FOUND", f"No tool registered: {tool_name}")

        # 2. Validate args
        errors = validate_tool_args(args, definition["parameters"])
        if errors:
            return ToolResult.fail(
                "SCHEMA_VALIDATION_FAILED",
                f"Argument validation failed: {'; '.join(errors)}",
            )

        # 3. Determine adapter
        adapter_name = definition["adapter"]
        adapter = self._adapters.get(adapter_name)
        if adapter is None:
            return ToolResult.fail(
                "ADAPTER_NOT_AVAILABLE",
                f"Adapter '{adapter_name}' is not available",
            )

        # 4. Load handler and execute
        try:
            handler = self._load_handler(tool_name)
            result = await adapter.execute(tool_name, args, handler)
            elapsed_ms = int((time.perf_counter_ns() - start) / 1_000_000)
            result.duration_ms = elapsed_ms
            return result
        except Exception as e:
            elapsed_ms = int((time.perf_counter_ns() - start) / 1_000_000)
            logger.exception("Handler error for tool %s", tool_name)
            return ToolResult.fail("HANDLER_ERROR", str(e), duration_ms=elapsed_ms)

    def _load_handler(self, tool_name: str) -> Any:
        """Load the handler module for a tool.

        Handler module name is the tool name with dots replaced by underscores.
        The module must have an `execute(args)` async function.
        """
        if tool_name in self._handler_cache:
            return self._handler_cache[tool_name]

        module_name = tool_name.replace(".", "_")
        module_path = f"orchestrator.handlers.{module_name}"

        try:
            module = importlib.import_module(module_path)
        except ModuleNotFoundError:
            raise ModuleNotFoundError(
                f"No handler module found at {module_path} "
                f"(expected file: handlers/{module_name}.py)"
            )

        if not hasattr(module, "execute"):
            raise AttributeError(
                f"Handler module {module_path} must define an 'execute' function"
            )

        self._handler_cache[tool_name] = module
        return module
