"""Tests for the dispatcher routing logic."""

import pytest
from unittest.mock import AsyncMock, MagicMock

from orchestrator.dispatcher.dispatcher import Dispatcher, _coerce_args
from orchestrator.dispatcher.result import ToolResult
from orchestrator.registry.registry import ToolRegistry


def _make_definition(name="revit.test_tool", adapter="revit"):
    return {
        "name": name,
        "adapter": adapter,
        "description": "A test tool for dispatcher testing purposes",
        "parameters": {
            "type": "object",
            "properties": {"arg1": {"type": "string"}},
            "required": ["arg1"],
        },
    }


@pytest.fixture
def registry():
    reg = ToolRegistry()
    reg.register(_make_definition())
    return reg


@pytest.fixture
def mock_adapter():
    adapter = AsyncMock()
    adapter.execute.return_value = ToolResult.ok({"result": "success"})
    return adapter


@pytest.fixture
def dispatcher(registry, mock_adapter, tmp_path):
    return Dispatcher(
        registry=registry,
        adapters={"revit": mock_adapter},
        handlers_dir=tmp_path,
    )


class TestDispatcher:
    async def test_tool_not_found(self, dispatcher):
        result = await dispatcher.dispatch("revit.nonexistent", {})
        assert result.success is False
        assert result.error_code == "TOOL_NOT_FOUND"

    async def test_schema_validation_failed(self, dispatcher):
        result = await dispatcher.dispatch("revit.test_tool", {})  # Missing required arg1
        assert result.success is False
        assert result.error_code == "SCHEMA_VALIDATION_FAILED"

    async def test_missing_handler_passes_none_to_adapter(self, dispatcher, mock_adapter):
        """When no handler module exists, dispatcher passes None to the adapter."""
        result = await dispatcher.dispatch("revit.test_tool", {"arg1": "hello"})
        # Should call the adapter, not fail with HANDLER_ERROR
        assert mock_adapter.execute.called
        call_args = mock_adapter.execute.call_args
        assert call_args[0][0] == "revit.test_tool"
        assert call_args[0][2] is None  # handler should be None

    async def test_coerce_string_encoded_array(self, registry, mock_adapter, tmp_path):
        """String-encoded arrays should be coerced to native lists before validation."""
        # Register a tool with an array parameter
        array_def = {
            "name": "revit.array_tool",
            "adapter": "revit",
            "description": "Tool with array params",
            "parameters": {
                "type": "object",
                "properties": {
                    "coords": {"type": "array", "items": {"type": "number"}},
                    "label": {"type": "string"},
                },
                "required": ["coords"],
            },
        }
        registry.register(array_def)
        d = Dispatcher(registry=registry, adapters={"revit": mock_adapter}, handlers_dir=tmp_path)

        # Pass coords as a string (the old bug)
        result = await d.dispatch("revit.array_tool", {"coords": "[1.0, 2.0, 3.0]", "label": "ok"})
        assert result.success is True
        # Verify adapter received the coerced list
        call_args = mock_adapter.execute.call_args[0]
        assert call_args[1]["coords"] == [1.0, 2.0, 3.0]

    async def test_success_path(self, dispatcher, mock_adapter, tmp_path):
        # Create a mock handler module
        handler_file = tmp_path / "revit_test_tool.py"
        handler_file.write_text(
            "async def execute(args, **kwargs):\n"
            "    from orchestrator.dispatcher.result import ToolResult\n"
            "    return ToolResult.ok({'result': 'success'})\n"
        )

        import sys
        import importlib.util
        spec = importlib.util.spec_from_file_location("orchestrator.handlers.revit_test_tool", str(handler_file))
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        sys.modules["orchestrator.handlers.revit_test_tool"] = mod

        try:
            result = await dispatcher.dispatch("revit.test_tool", {"arg1": "hello"})
            assert mock_adapter.execute.called
        finally:
            sys.modules.pop("orchestrator.handlers.revit_test_tool", None)


class TestCoerceArgs:
    """Unit tests for the _coerce_args helper."""

    def test_coerces_string_array_to_list(self):
        schema = {"properties": {"pts": {"type": "array"}}}
        result = _coerce_args({"pts": "[1,2,3]"}, schema)
        assert result["pts"] == [1, 2, 3]

    def test_coerces_string_object_to_dict(self):
        schema = {"properties": {"meta": {"type": "object"}}}
        result = _coerce_args({"meta": '{"a": 1}'}, schema)
        assert result["meta"] == {"a": 1}

    def test_leaves_valid_array_untouched(self):
        schema = {"properties": {"pts": {"type": "array"}}}
        result = _coerce_args({"pts": [1, 2, 3]}, schema)
        assert result["pts"] == [1, 2, 3]

    def test_leaves_string_when_schema_expects_string(self):
        schema = {"properties": {"name": {"type": "string"}}}
        result = _coerce_args({"name": "[not,an,array]"}, schema)
        assert result["name"] == "[not,an,array]"

    def test_leaves_invalid_json_as_string(self):
        schema = {"properties": {"pts": {"type": "array"}}}
        result = _coerce_args({"pts": "[not valid json"}, schema)
        assert result["pts"] == "[not valid json"

    def test_no_properties_in_schema(self):
        schema = {"type": "object"}
        args = {"x": "[1,2]"}
        assert _coerce_args(args, schema) == args

    def test_unknown_key_not_coerced(self):
        schema = {"properties": {"pts": {"type": "array"}}}
        result = _coerce_args({"other": "[1,2]"}, schema)
        assert result["other"] == "[1,2]"
