"""JSON Schema validation for tool definitions and tool call arguments."""

from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any

import jsonschema

logger = logging.getLogger(__name__)

_CONTRACTS_DIR = Path(__file__).parent.parent.parent.parent.parent / "contracts"


def _load_schema(name: str) -> dict[str, Any]:
    schema_path = _CONTRACTS_DIR / name
    with open(schema_path, encoding="utf-8") as f:
        return json.load(f)


_tool_definition_schema: dict[str, Any] | None = None


def get_tool_definition_schema() -> dict[str, Any]:
    """Return the cached tool definition JSON Schema."""
    global _tool_definition_schema
    if _tool_definition_schema is None:
        _tool_definition_schema = _load_schema("tool-definition.schema.json")
    return _tool_definition_schema


def validate_tool_definition(definition: dict[str, Any]) -> list[str]:
    """Validate a tool definition against the schema.

    Returns a list of validation error messages (empty if valid).
    """
    schema = get_tool_definition_schema()
    validator = jsonschema.Draft202012Validator(schema)
    return [e.message for e in validator.iter_errors(definition)]


def validate_tool_args(
    args: dict[str, Any], parameters_schema: dict[str, Any]
) -> list[str]:
    """Validate tool call arguments against the tool's parameter schema.

    Returns a list of validation error messages (empty if valid).
    """
    validator = jsonschema.Draft202012Validator(parameters_schema)
    return [e.message for e in validator.iter_errors(args)]


def get_tool_permissions(definition: dict[str, Any]) -> dict[str, Any]:
    """Extract permissions from a tool definition with defaults.

    Returns a dict with at least 'mode' and 'approval_required'.
    """
    perms = definition.get("permissions", {})
    return {
        "mode": perms.get("mode", "write"),
        "categories": perms.get("categories", []),
        "approval_required": perms.get("approval_required", False),
    }


def get_tool_side_effects(definition: dict[str, Any]) -> list[str]:
    """Extract side effects from a tool definition.

    Returns a list of side effect strings, or empty list if none specified.
    """
    return list(definition.get("side_effects", []))


def get_execution_modes(definition: dict[str, Any]) -> list[str]:
    """Extract supported execution modes from a tool definition.

    Returns a list of mode strings (e.g. ["headless", "interactive"]).
    Defaults to ["headless"] if not specified.
    """
    return definition.get("execution", {}).get("modes", ["headless"])
