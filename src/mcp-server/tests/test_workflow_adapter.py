"""Tests for the workflow adapter routing logic."""

import pytest
from unittest.mock import AsyncMock, MagicMock

from orchestrator.adapters.workflow import WorkflowAdapter
from orchestrator.dispatcher.result import ToolResult
from orchestrator.registry.registry import ToolRegistry


@pytest.fixture
def mock_dispatcher():
    d = AsyncMock()
    d.dispatch.return_value = ToolResult.ok({"element_id": 1})
    return d


@pytest.fixture
def registry():
    reg = ToolRegistry()
    reg.register({
        "name": "flow.declarative_test",
        "adapter": "workflow",
        "description": "A declarative workflow test for adapter routing verification",
        "parameters": {
            "type": "object",
            "properties": {},
        },
        "workflow": {
            "steps": [
                {"id": "s1", "tool": "revit.create_wall", "args": {"height": 10}},
            ]
        },
    })
    reg.register({
        "name": "flow.handler_test",
        "adapter": "workflow",
        "description": "A handler-based workflow test for adapter routing verification",
        "parameters": {
            "type": "object",
            "properties": {},
        },
    })
    return reg


class TestWorkflowAdapter:
    async def test_declarative_routes_to_engine(self, mock_dispatcher, registry):
        adapter = WorkflowAdapter(registry=registry)
        adapter.set_dispatcher(mock_dispatcher)

        # Dummy handler (should not be used for declarative workflows)
        handler = MagicMock()
        result = await adapter.execute("flow.declarative_test", {}, handler)

        assert result.success is True
        assert mock_dispatcher.dispatch.called
        handler.execute.assert_not_called()

    async def test_handler_routes_to_handler(self, mock_dispatcher, registry):
        adapter = WorkflowAdapter(registry=registry)
        adapter.set_dispatcher(mock_dispatcher)

        handler = AsyncMock()
        handler.execute.return_value = ToolResult.ok({"result": "done"})
        result = await adapter.execute("flow.handler_test", {}, handler)

        assert result.success is True
        handler.execute.assert_called_once()

    async def test_no_dispatcher_returns_error(self, registry):
        adapter = WorkflowAdapter(registry=registry)
        handler = MagicMock()
        result = await adapter.execute("flow.declarative_test", {}, handler)

        assert result.success is False
        assert result.error_code == "ADAPTER_NOT_AVAILABLE"
