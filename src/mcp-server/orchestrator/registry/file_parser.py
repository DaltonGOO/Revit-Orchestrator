"""Auto-parse .dyn, .py, and .cs files to generate tool definitions."""

from __future__ import annotations

import ast
import json
import logging
import re
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)


def parse_file(path: Path) -> dict[str, Any]:
    """Detect file type by extension and generate a partial tool definition.

    Returns a dict with ``name``, ``adapter``, ``description``, ``parameters``
    pre-filled where possible.  Fields that cannot be auto-detected get
    placeholder values so the caller can prompt the user.
    """
    suffix = path.suffix.lower()
    if suffix == ".dyn":
        return _parse_dynamo(path)
    if suffix == ".py":
        return _parse_python(path)
    if suffix == ".cs":
        return _parse_csharp(path)
    raise ValueError(f"Unsupported file type: {suffix}")


# ---------------------------------------------------------------------------
# Dynamo (.dyn)
# ---------------------------------------------------------------------------

def _parse_dynamo(path: Path) -> dict[str, Any]:
    """Parse a Dynamo graph JSON file."""
    with open(path, encoding="utf-8") as f:
        graph = json.load(f)

    # Graph-level description
    description = graph.get("Description", "") or ""

    # Build a readable name from the filename
    name = _name_from_path(path, "dynamo")

    # Collect input nodes — Dynamo uses various node types for inputs
    parameters: dict[str, Any] = {
        "type": "object",
        "properties": {
            "graph_path": {
                "type": "string",
                "description": f"Path to the Dynamo graph file (default: {path})",
            },
            "inputs": {
                "type": "object",
                "description": "Key-value pairs mapping Dynamo input node names to their values",
                "additionalProperties": True,
            },
        },
        "required": ["graph_path"],
    }

    # Try to extract input node names from the graph for documentation
    input_nodes = _extract_dynamo_inputs(graph)
    if input_nodes:
        description += f"\n\nDetected input nodes: {', '.join(input_nodes)}"
        description = description.strip()

    # Fallback description if none found (schema requires min 10 chars)
    if not description:
        description = f"Dynamo graph: {path.stem}"

    return {
        "name": name,
        "adapter": "dynamo",
        "description": description,
        "parameters": parameters,
        "returns": {
            "type": "object",
            "properties": {
                "outputs": {"type": "object"},
                "message": {"type": "string"},
            },
        },
        "examples": [],
        "_source_path": str(path),
    }


def _extract_dynamo_inputs(graph: dict[str, Any]) -> list[str]:
    """Extract input node names from a Dynamo graph."""
    inputs: list[str] = []
    for node in graph.get("Nodes", []):
        node_type = node.get("NodeType", "")
        concrete = node.get("ConcreteType", "")
        # Common Dynamo input node types
        if "Input" in node_type or "Symbol" in concrete:
            nick = node.get("Name", "") or node.get("NickName", "")
            if nick:
                inputs.append(nick)
    return inputs


# ---------------------------------------------------------------------------
# Python (.py)
# ---------------------------------------------------------------------------

def _parse_python(path: Path) -> dict[str, Any]:
    """Parse a Python script to extract description and parameters."""
    source = path.read_text(encoding="utf-8")
    name = _name_from_path(path, "pyrevit")

    description = ""
    properties: dict[str, Any] = {}
    required: list[str] = []

    try:
        tree = ast.parse(source)

        # Module-level docstring
        description = ast.get_docstring(tree) or ""

        # Look for a top-level main() or execute() function
        for node in ast.iter_child_nodes(tree):
            if isinstance(node, ast.FunctionDef) and node.name in ("main", "execute", "run"):
                func_doc = ast.get_docstring(node)
                if func_doc and not description:
                    description = func_doc
                for arg in node.args.args:
                    if arg.arg == "self":
                        continue
                    param_name = arg.arg
                    properties[param_name] = {
                        "type": "string",
                        "description": "",
                    }
                    # If no default, it's required
                    required.append(param_name)
                # Adjust required list for defaults
                n_defaults = len(node.args.defaults)
                if n_defaults:
                    required = required[:-n_defaults]
                break
    except SyntaxError:
        logger.warning("Could not parse Python file: %s", path)

    parameters: dict[str, Any] = {
        "type": "object",
        "properties": {
            "script_path": {
                "type": "string",
                "description": f"Path to the Python script (default: {path})",
            },
            "arguments": {
                "type": "object",
                "description": "Key-value arguments to pass to the script",
                "additionalProperties": {"type": "string"},
            },
        },
        "required": ["script_path"],
    }

    # If we extracted function params, document them in the description
    if properties:
        param_list = ", ".join(properties.keys())
        description += f"\n\nDetected parameters: {param_list}"
        description = description.strip()

    # Fallback description if none found (schema requires min 10 chars)
    if not description:
        description = f"Python script: {path.stem}"

    return {
        "name": name,
        "adapter": "pyrevit",
        "description": description,
        "parameters": parameters,
        "returns": {
            "type": "object",
            "properties": {
                "stdout": {"type": "string"},
                "stderr": {"type": "string"},
                "exit_code": {"type": "integer"},
            },
        },
        "examples": [],
        "_source_path": str(path),
    }


# ---------------------------------------------------------------------------
# C# (.cs)
# ---------------------------------------------------------------------------

def _parse_csharp(path: Path) -> dict[str, Any]:
    """Parse a C# file using regex to extract class name, description, and parameters."""
    source = path.read_text(encoding="utf-8")
    name = _name_from_path(path, "revit")

    description = ""
    properties: dict[str, Any] = {}
    required: list[str] = []

    # Extract XML doc <summary> for the class
    summary_match = re.search(
        r"///\s*<summary>\s*\n(.*?)\n\s*///\s*</summary>\s*\n\s*(?:public\s+)?class",
        source,
        re.DOTALL,
    )
    if summary_match:
        raw = summary_match.group(1)
        # Strip /// prefixes
        lines = [re.sub(r"^\s*///\s?", "", line) for line in raw.splitlines()]
        description = " ".join(line.strip() for line in lines if line.strip())

    # Look for an Execute method and extract its parameters
    execute_match = re.search(
        r"(?:public|internal)\s+\w+\s+Execute\s*\(([^)]*)\)",
        source,
    )
    if execute_match:
        params_str = execute_match.group(1).strip()
        if params_str:
            for param in params_str.split(","):
                param = param.strip()
                parts = param.split()
                if len(parts) >= 2:
                    param_name = parts[-1]
                    properties[param_name] = {
                        "type": "string",
                        "description": "",
                    }
                    required.append(param_name)

    # Extract class name for a better tool name
    class_match = re.search(r"class\s+(\w+)", source)
    if class_match:
        class_name = class_match.group(1)
        # Convert PascalCase to snake_case
        snake = re.sub(r"(?<!^)(?=[A-Z])", "_", class_name).lower()
        name = f"revit.{snake}"

    parameters: dict[str, Any] = {
        "type": "object",
        "properties": properties if properties else {},
        "required": required,
    }

    # Fallback description if none found (schema requires min 10 chars)
    if not description:
        description = f"Revit command: {path.stem}"

    return {
        "name": name,
        "adapter": "revit",
        "description": description,
        "parameters": parameters,
        "returns": {
            "type": "object",
            "properties": {
                "result": {"type": "string"},
            },
        },
        "examples": [],
        "_source_path": str(path),
    }


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _name_from_path(path: Path, adapter_prefix: str) -> str:
    """Generate a tool name from a file path."""
    stem = path.stem
    # Convert PascalCase / camelCase to snake_case
    snake = re.sub(r"(?<!^)(?=[A-Z])", "_", stem).lower()
    # Replace spaces / hyphens with underscores
    snake = re.sub(r"[\s\-]+", "_", snake)
    return f"{adapter_prefix}.{snake}"
