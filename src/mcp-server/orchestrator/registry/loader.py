"""Loads tool definition JSON files from disk."""

from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)


def load_tool_file(path: Path) -> dict[str, Any]:
    """Load and parse a single tool definition JSON file.

    Raises:
        FileNotFoundError: If the file does not exist.
        json.JSONDecodeError: If the file is not valid JSON.
    """
    with open(path, encoding="utf-8") as f:
        return json.load(f)


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
