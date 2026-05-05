"""Handler for dynamo.describe_graph — extracts inputs/outputs from a .dyn file.

Reads the .dyn file directly (it's JSON) and returns a structured summary so the
LLM can know what inputs a graph takes and what outputs it produces *before*
calling dynamo.run_graph. Pure file read — no Revit, no Dynamo SDK.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from ..dispatcher.result import ToolResult


# Map .dyn `Type` values to the JSON-schema-style names the LLM understands.
# Dynamo's Type fields use these spellings; anything not in this map is
# returned verbatim so the LLM can still see something useful.
_TYPE_NORMALISE = {
    "string": "string",
    "number": "number",
    "double": "number",
    "int": "integer",
    "integer": "integer",
    "bool": "boolean",
    "boolean": "boolean",
}


async def execute(args: dict[str, Any], **kwargs: Any) -> ToolResult:
    graph_path_raw = args.get("graph_path", "")
    graph_path = Path(graph_path_raw).expanduser() if graph_path_raw else None
    if graph_path is None or not str(graph_path):
        return ToolResult.fail("INVALID_ARGUMENT", "graph_path is required")

    if not graph_path.exists():
        return ToolResult.fail(
            "FILE_NOT_FOUND",
            f"Graph file not found: {graph_path}",
        )

    try:
        with graph_path.open("r", encoding="utf-8") as f:
            doc = json.load(f)
    except json.JSONDecodeError as e:
        return ToolResult.fail(
            "DYNAMO_GRAPH_PARSE_ERROR",
            f"Could not parse .dyn as JSON: {e}",
        )
    except OSError as e:
        return ToolResult.fail(
            "DYNAMO_GRAPH_READ_ERROR",
            f"Could not read .dyn file: {e}",
        )

    inputs = _extract_io(doc, kind="input")
    outputs = _extract_io(doc, kind="output")

    return ToolResult.ok({
        "graph_path": str(graph_path),
        "name": doc.get("Name", "") or graph_path.stem,
        "description": doc.get("Description", "") or "",
        "inputs": inputs,
        "outputs": outputs,
    })


def _extract_io(doc: dict[str, Any], *, kind: str) -> list[dict[str, Any]]:
    """Return the list of input or output node descriptors for a .dyn document.

    Strategy:
      1. Trust the top-level ``Inputs`` / ``Outputs`` array if it has entries —
         Dynamo writes that summary on save with rich type/value info.
      2. Otherwise, fall back to scanning ``View.NodeViews`` for nodes flagged
         ``IsSetAsInput`` / ``IsSetAsOutput`` and synthesising minimal entries
         (just ``name``) so the LLM at least sees the field exists.
    """
    summary_key = "Inputs" if kind == "input" else "Outputs"
    flag_key = "IsSetAsInput" if kind == "input" else "IsSetAsOutput"

    summary = doc.get(summary_key)
    if isinstance(summary, list) and summary:
        return [_normalise_summary_entry(entry) for entry in summary if isinstance(entry, dict)]

    # Fallback: walk NodeViews for the flag
    views = (doc.get("View") or {}).get("NodeViews") or []
    fallback: list[dict[str, Any]] = []
    for view in views:
        if not isinstance(view, dict):
            continue
        if view.get(flag_key) is True:
            fallback.append({
                "name": view.get("Name", "") or "",
                "type": "unknown",
                "default": None,
                "description": "",
            })
    return fallback


def _normalise_summary_entry(entry: dict[str, Any]) -> dict[str, Any]:
    raw_type = (entry.get("Type") or entry.get("Type2") or "").strip().lower()
    return {
        "name": entry.get("Name", "") or "",
        "type": _TYPE_NORMALISE.get(raw_type, raw_type or "unknown"),
        "default": entry.get("Value"),
        "description": entry.get("Description", "") or "",
    }
