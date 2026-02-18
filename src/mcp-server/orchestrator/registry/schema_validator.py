"""JSON Schema validation for tool definitions and tool call arguments."""

from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any

import jsonschema

logger = logging.getLogger(__name__)

def _find_contracts_dir() -> Path:
    """Locate the ``contracts/`` directory by walking up from this file.

    Uses ``resolve()`` first so NTFS junctions / symlinks are followed
    before climbing the directory tree.
    """
    p = Path(__file__).resolve()
    for _ in range(10):
        p = p.parent
        candidate = p / "contracts"
        if candidate.is_dir():
            return candidate
    raise FileNotFoundError(
        "Could not find 'contracts' directory above " + str(Path(__file__).resolve())
    )


_CONTRACTS_DIR = _find_contracts_dir()


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


def validate_tool_args_detailed(
    args: dict[str, Any], parameters_schema: dict[str, Any]
) -> list[dict[str, Any]]:
    """Validate tool call arguments and return structured error details.

    Each error dict contains:
      - message: Human-readable error message
      - argument_path: JSON path to the offending value (e.g. "$.width")
      - expected: Expected type or schema constraint description
      - received_value: The actual value that failed validation
      - received_type: Python type name of the received value
    """
    validator = jsonschema.Draft202012Validator(parameters_schema)
    details: list[dict[str, Any]] = []
    for error in validator.iter_errors(args):
        path = "$" + "".join(
            f".{p}" if isinstance(p, str) else f"[{p}]"
            for p in error.absolute_path
        )
        # Build a concise expected description
        if error.validator == "type":
            expected = f"type '{error.validator_value}'"
        elif error.validator == "required":
            expected = f"required property: {error.message}"
        elif error.validator == "enum":
            expected = f"one of {error.validator_value}"
        else:
            expected = f"{error.validator}: {error.validator_value}"

        details.append({
            "message": error.message,
            "argument_path": path,
            "expected": expected,
            "received_value": _safe_repr(error.instance),
            "received_type": type(error.instance).__name__,
        })
    return details


def _safe_repr(value: Any) -> Any:
    """Return a JSON-safe representation of a value for error reporting."""
    if value is None or isinstance(value, (bool, int, float, str)):
        return value
    if isinstance(value, (list, dict)):
        # Truncate large values
        s = json.dumps(value, default=str)
        if len(s) > 200:
            return s[:200] + "..."
        return value
    return str(value)


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
