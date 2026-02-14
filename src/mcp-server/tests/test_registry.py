"""Tests for the tool registry."""

import pytest

from orchestrator.registry.registry import ToolRegistry
from orchestrator.registry.schema_validator import validate_tool_definition


def _make_valid_definition(name="revit.test_tool", **overrides):
    base = {
        "name": name,
        "adapter": "revit",
        "description": "A test tool for registry testing purposes",
        "parameters": {
            "type": "object",
            "properties": {"arg1": {"type": "string"}},
        },
    }
    base.update(overrides)
    return base


class TestToolRegistry:
    def test_register_and_get(self):
        reg = ToolRegistry()
        defn = _make_valid_definition()
        reg.register(defn)
        assert reg.get("revit.test_tool") is not None

    def test_get_nonexistent_returns_none(self):
        reg = ToolRegistry()
        assert reg.get("revit.nonexistent") is None

    def test_unregister(self):
        reg = ToolRegistry()
        defn = _make_valid_definition()
        reg.register(defn)
        assert reg.unregister("revit.test_tool") is True
        assert reg.get("revit.test_tool") is None

    def test_unregister_nonexistent_returns_false(self):
        reg = ToolRegistry()
        assert reg.unregister("revit.nope") is False

    def test_list_tools(self):
        reg = ToolRegistry()
        reg.register(_make_valid_definition("revit.tool_a"))
        reg.register(_make_valid_definition("revit.tool_b"))
        names = reg.list_tool_names()
        assert "revit.tool_a" in names
        assert "revit.tool_b" in names

    def test_invalid_definition_rejected(self):
        reg = ToolRegistry()
        with pytest.raises(ValueError, match="Invalid tool definition"):
            reg.register({"name": "bad"})  # Missing required fields

    def test_list_tools_by_tag(self):
        reg = ToolRegistry()
        reg.register(_make_valid_definition("revit.tool_a", tags=["geometry", "walls"]))
        reg.register(_make_valid_definition("revit.tool_b", tags=["query"]))
        reg.register(_make_valid_definition("revit.tool_c", tags=["geometry"]))

        geo_tools = reg.list_tools_by_tag("geometry")
        assert len(geo_tools) == 2
        names = [t["name"] for t in geo_tools]
        assert "revit.tool_a" in names
        assert "revit.tool_c" in names

    def test_list_tools_by_tag_empty(self):
        reg = ToolRegistry()
        reg.register(_make_valid_definition())
        assert reg.list_tools_by_tag("nonexistent") == []

    def test_get_by_version(self):
        reg = ToolRegistry()
        reg.register(_make_valid_definition(version="1.0.0"))
        assert reg.get_by_version("revit.test_tool", "1.0.0") is not None
        assert reg.get_by_version("revit.test_tool", "2.0.0") is None

    def test_get_by_version_no_version_field(self):
        reg = ToolRegistry()
        reg.register(_make_valid_definition())  # No version
        assert reg.get_by_version("revit.test_tool", "1.0.0") is None

    def test_load_from_directory(self, mock_registry):
        """The mock_registry fixture loads from fixtures/tools/."""
        names = mock_registry.list_tool_names()
        assert "revit.create_wall" in names
        assert "revit.get_element_info" in names
