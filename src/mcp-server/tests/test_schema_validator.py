"""Tests for schema validation of tool definitions."""

import pytest

from orchestrator.registry.schema_validator import (
    validate_tool_definition,
    validate_tool_args,
    get_tool_permissions,
    get_tool_side_effects,
)


def _make_valid_definition(**overrides):
    """Create a minimal valid tool definition with optional overrides."""
    base = {
        "name": "revit.test_tool",
        "adapter": "revit",
        "description": "A test tool for validation testing purposes",
        "parameters": {
            "type": "object",
            "properties": {
                "arg1": {"type": "string"}
            },
        },
    }
    base.update(overrides)
    return base


class TestValidateToolDefinition:
    def test_valid_minimal_definition(self):
        defn = _make_valid_definition()
        assert validate_tool_definition(defn) == []

    def test_valid_full_definition(self, mock_registry):
        """Tools loaded from fixtures should validate."""
        tool = mock_registry.get("revit.create_wall")
        assert tool is not None
        assert validate_tool_definition(tool) == []

    def test_missing_name_fails(self):
        defn = _make_valid_definition()
        del defn["name"]
        errors = validate_tool_definition(defn)
        assert len(errors) > 0
        assert any("name" in e.lower() for e in errors)

    def test_bad_adapter_fails(self):
        defn = _make_valid_definition(adapter="invalid_adapter")
        errors = validate_tool_definition(defn)
        assert len(errors) > 0

    def test_old_tools_without_new_fields_still_validate(self):
        """Tools without version, permissions, tags etc. remain valid."""
        defn = _make_valid_definition()
        assert validate_tool_definition(defn) == []

    def test_valid_version_passes(self):
        defn = _make_valid_definition(version="1.0.0")
        assert validate_tool_definition(defn) == []

    def test_bad_semver_version_fails(self):
        defn = _make_valid_definition(version="not-a-version")
        errors = validate_tool_definition(defn)
        assert len(errors) > 0

    def test_valid_permissions_passes(self):
        defn = _make_valid_definition(
            permissions={"mode": "read", "categories": ["Walls"]}
        )
        assert validate_tool_definition(defn) == []

    def test_invalid_permission_mode_fails(self):
        defn = _make_valid_definition(
            permissions={"mode": "execute"}
        )
        errors = validate_tool_definition(defn)
        assert len(errors) > 0

    def test_valid_side_effects_passes(self):
        defn = _make_valid_definition(
            side_effects=["creates_elements", "modifies_elements"]
        )
        assert validate_tool_definition(defn) == []

    def test_invalid_side_effect_fails(self):
        defn = _make_valid_definition(
            side_effects=["explodes_building"]
        )
        errors = validate_tool_definition(defn)
        assert len(errors) > 0

    def test_valid_preconditions_passes(self):
        defn = _make_valid_definition(
            preconditions=[{"check": "document_open"}]
        )
        assert validate_tool_definition(defn) == []

    def test_valid_cost_passes(self):
        defn = _make_valid_definition(
            cost={"estimated_duration_ms": 500, "cacheable": True}
        )
        assert validate_tool_definition(defn) == []

    def test_valid_tags_passes(self):
        defn = _make_valid_definition(tags=["geometry", "walls"])
        assert validate_tool_definition(defn) == []

    def test_deprecated_field_passes(self):
        defn = _make_valid_definition(
            deprecated=True, superseded_by="revit.test_tool_v2"
        )
        assert validate_tool_definition(defn) == []

    def test_valid_workflow_passes(self):
        defn = _make_valid_definition(
            adapter="workflow",
            workflow={
                "steps": [
                    {"tool": "revit.create_wall", "args": {"height": 10}},
                    {"tool": "revit.get_element_info", "bindings": {"element_id": "$steps.step1.data.element_id"}},
                ]
            },
        )
        assert validate_tool_definition(defn) == []

    def test_additional_properties_rejected(self):
        defn = _make_valid_definition(unknown_field="bad")
        errors = validate_tool_definition(defn)
        assert len(errors) > 0


class TestValidateToolArgs:
    def test_valid_args_pass(self):
        schema = {
            "type": "object",
            "properties": {"x": {"type": "integer"}},
            "required": ["x"],
        }
        assert validate_tool_args({"x": 42}, schema) == []

    def test_missing_required_arg_fails(self):
        schema = {
            "type": "object",
            "properties": {"x": {"type": "integer"}},
            "required": ["x"],
        }
        errors = validate_tool_args({}, schema)
        assert len(errors) > 0

    def test_wrong_type_fails(self):
        schema = {
            "type": "object",
            "properties": {"x": {"type": "integer"}},
        }
        errors = validate_tool_args({"x": "not_int"}, schema)
        assert len(errors) > 0


class TestGetToolPermissions:
    def test_defaults_when_missing(self):
        perms = get_tool_permissions({})
        assert perms["mode"] == "write"
        assert perms["approval_required"] is False
        assert perms["categories"] == []

    def test_extracts_existing_permissions(self):
        defn = {"permissions": {"mode": "read", "approval_required": True, "categories": ["Walls"]}}
        perms = get_tool_permissions(defn)
        assert perms["mode"] == "read"
        assert perms["approval_required"] is True
        assert perms["categories"] == ["Walls"]


class TestGetToolSideEffects:
    def test_empty_when_missing(self):
        assert get_tool_side_effects({}) == []

    def test_extracts_effects(self):
        defn = {"side_effects": ["creates_elements", "modifies_elements"]}
        assert get_tool_side_effects(defn) == ["creates_elements", "modifies_elements"]
