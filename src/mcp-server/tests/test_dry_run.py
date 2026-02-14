"""Tests for dry-run mode in the dispatcher."""

import pytest
from unittest.mock import AsyncMock

from orchestrator.dispatcher.dispatcher import Dispatcher
from orchestrator.dispatcher.result import ToolResult
from orchestrator.registry.registry import ToolRegistry


def _make_tool(name="revit.test_tool"):
    return {
        "name": name,
        "adapter": "revit",
        "description": "A test tool for dry-run mode testing purposes",
        "parameters": {
            "type": "object",
            "properties": {"arg1": {"type": "string"}},
            "required": ["arg1"],
        },
        "permissions": {"mode": "write", "categories": ["Walls"]},
        "side_effects": ["creates_elements"],
    }


@pytest.fixture
def registry():
    reg = ToolRegistry()
    reg.register(_make_tool())
    return reg


@pytest.fixture
def mock_adapter():
    adapter = AsyncMock()
    adapter.execute.return_value = ToolResult.ok({"result": "executed"})
    return adapter


class TestDryRun:
    async def test_dry_run_returns_plan(self, registry, mock_adapter, tmp_path):
        dispatcher = Dispatcher(
            registry=registry,
            adapters={"revit": mock_adapter},
            handlers_dir=tmp_path,
        )
        result = await dispatcher.dispatch(
            "revit.test_tool", {"arg1": "hello"}, dry_run=True
        )

        assert result.success is True
        assert result.data["dry_run"] is True
        assert result.data["tool_name"] == "revit.test_tool"
        assert result.data["permissions"]["mode"] == "write"
        assert "creates_elements" in result.data["side_effects"]
        mock_adapter.execute.assert_not_called()

    async def test_dry_run_still_validates_args(self, registry, mock_adapter, tmp_path):
        dispatcher = Dispatcher(
            registry=registry,
            adapters={"revit": mock_adapter},
            handlers_dir=tmp_path,
        )
        result = await dispatcher.dispatch(
            "revit.test_tool", {}, dry_run=True  # Missing required arg1
        )

        assert result.success is False
        assert result.error_code == "SCHEMA_VALIDATION_FAILED"

    async def test_dry_run_checks_preconditions(self, registry, mock_adapter, tmp_path):
        """If precondition checker is provided and fails, dry-run also fails."""
        checker = AsyncMock()
        checker.check.return_value = ["Document not open"]

        dispatcher = Dispatcher(
            registry=registry,
            adapters={"revit": mock_adapter},
            handlers_dir=tmp_path,
            precondition_checker=checker,
        )
        result = await dispatcher.dispatch(
            "revit.test_tool", {"arg1": "hello"}, dry_run=True
        )

        assert result.success is False
        assert result.error_code == "PRECONDITION_FAILED"
