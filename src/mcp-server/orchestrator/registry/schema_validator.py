"""JSON Schema validation for tool definitions and tool call arguments."""

from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any

import jsonschema

logger = logging.getLogger(__name__)

_CONTRACTS_DIR = Path(__file__).parent.parent.parent.parent / "contracts"


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
