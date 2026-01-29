"""FastMCP entry point with dynamic tool registration."""

from __future__ import annotations

import logging
from typing import Any

from mcp.server.fastmcp import FastMCP

from .config import Config
from .registry.registry import ToolRegistry
from .dispatcher.dispatcher import Dispatcher
from .dispatcher.result import ToolResult
from .adapters.revit_addin import RevitAddinAdapter
from .adapters.pyrevit import PyRevitAdapter
from .adapters.dynamo import DynamoAdapter
from .adapters.workflow import WorkflowAdapter

logger = logging.getLogger(__name__)

# Global instances
config = Config.defaults()
registry = ToolRegistry()
mcp = FastMCP("Revit Orchestrator")

# Adapters
revit_adapter = RevitAddinAdapter()
pyrevit_adapter = PyRevitAdapter()
dynamo_adapter = DynamoAdapter()
workflow_adapter = WorkflowAdapter()

adapters: dict[str, Any] = {
    "revit": revit_adapter,
    "pyrevit": pyrevit_adapter,
    "dynamo": dynamo_adapter,
    "workflow": workflow_adapter,
}

dispatcher = Dispatcher(registry, adapters, config.handlers_dir)

# Wire up cross-references
dynamo_adapter.set_revit_adapter(revit_adapter)
workflow_adapter.set_dispatcher(dispatcher)


def _register_mcp_tools() -> None:
    """Register all tools from the registry as MCP tools."""
    for definition in registry.list_tools():
        _register_single_tool(definition)


def _register_single_tool(definition: dict[str, Any]) -> None:
    """Register a single tool definition as an MCP tool."""
    tool_name = definition["name"]
    description = definition["description"]
    parameters = definition["parameters"]

    # Create the tool function dynamically
    async def tool_handler(
        arguments: dict[str, Any],
        _name: str = tool_name,
    ) -> dict[str, Any]:
        result = await dispatcher.dispatch(_name, arguments)
        return result.to_dict()

    # Register with FastMCP
    mcp.tool(
        name=tool_name,
        description=description,
    )(tool_handler)

    logger.info("Registered MCP tool: %s", tool_name)


def init() -> None:
    """Initialize the server: load tools, start watchers."""
    global config
    config = Config.from_env()

    # Load tool definitions
    registry.load_from_directory(config.tools_dir)
    _register_mcp_tools()

    # Set up hot-reload
    if config.watch_tools_dir:
        registry.on_change(_register_mcp_tools)
        registry.start_watching(config.tools_dir)

    logger.info(
        "Revit Orchestrator MCP server initialized with %d tools",
        len(registry.list_tool_names()),
    )


# Initialize on import
init()

if __name__ == "__main__":
    mcp.run()
