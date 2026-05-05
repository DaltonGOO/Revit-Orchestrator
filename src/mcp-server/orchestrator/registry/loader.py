"""Loads tool definition JSON files from disk."""

from __future__ import annotations

import json
import logging
import os
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)


def _find_repo_root() -> Path:
    """Locate the root used to resolve ${REPO_ROOT} in tool defs.

    Two layouts are supported:
      * Dev clone:  <repo>/src/mcp-server/orchestrator/registry/loader.py
                    sibling `tools/` and `src/` directories.
      * Installed:  …/RevitOrchestrator/python-server/_internal/orchestrator/...
                    with a sibling `tools/` deployed next to `python-server/`.

    Override with the REVIT_ORCHESTRATOR_ROOT env var if neither matches.
    """
    env = os.environ.get("REVIT_ORCHESTRATOR_ROOT")
    if env:
        return Path(env)
    here = Path(__file__).resolve()
    # Strong signal first: dev clone (tools/ AND src/ as siblings).
    for parent in here.parents:
        if (parent / "tools").is_dir() and (parent / "src").is_dir():
            return parent
    # Weaker signal: any ancestor with a tools/ folder (installed layout).
    for parent in here.parents:
        if (parent / "tools").is_dir():
            return parent
    # Last resort: 4 dirs up from this file (matches dev clone shape).
    return here.parents[4] if len(here.parents) >= 5 else here.parent


_REPO_ROOT_CACHE: Path | None = None


def repo_root() -> Path:
    global _REPO_ROOT_CACHE
    if _REPO_ROOT_CACHE is None:
        _REPO_ROOT_CACHE = _find_repo_root()
    return _REPO_ROOT_CACHE


def _resolve_tokens(value: Any) -> Any:
    """Recursively replace ${REPO_ROOT} tokens in strings.

    Walks dicts/lists so a token used anywhere in a tool def (typically
    in a parameter `default`) gets resolved at load time.
    """
    if isinstance(value, str):
        if "${REPO_ROOT}" in value:
            return value.replace("${REPO_ROOT}", str(repo_root()))
        return value
    if isinstance(value, dict):
        return {k: _resolve_tokens(v) for k, v in value.items()}
    if isinstance(value, list):
        return [_resolve_tokens(v) for v in value]
    return value


def load_tool_file(path: Path) -> dict[str, Any]:
    """Load and parse a single tool definition JSON file.

    Raises:
        FileNotFoundError: If the file does not exist.
        json.JSONDecodeError: If the file is not valid JSON.
    """
    with open(path, encoding="utf-8") as f:
        definition = json.load(f)
    return _resolve_tokens(definition)


def load_all_tools(tools_dir: Path) -> dict[str, dict[str, Any]]:
    """Load all .json tool definitions from a directory.

    Returns a dict mapping tool name to its definition.
    """
    tools: dict[str, dict[str, Any]] = {}
    if not tools_dir.exists():
        logger.warning("Tools directory does not exist: %s", tools_dir)
        return tools

    for path in sorted(tools_dir.glob("*.json")):
        try:
            definition = load_tool_file(path)
            name = definition.get("name", path.stem)
            tools[name] = definition
            logger.info("Loaded tool: %s from %s", name, path.name)
        except (json.JSONDecodeError, KeyError) as e:
            logger.error("Failed to load tool from %s: %s", path.name, e)

    return tools
