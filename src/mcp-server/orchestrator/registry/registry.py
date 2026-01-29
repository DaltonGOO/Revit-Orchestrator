"""In-memory tool catalog with optional hot-reload via watchdog."""

from __future__ import annotations

import logging
import threading
from pathlib import Path
from typing import Any, Callable

from watchdog.events import FileSystemEventHandler, FileSystemEvent
from watchdog.observers import Observer

from .loader import load_all_tools, load_tool_file
from .schema_validator import validate_tool_definition

logger = logging.getLogger(__name__)


class ToolRegistry:
    """Thread-safe in-memory registry of tool definitions."""

    def __init__(self) -> None:
        self._tools: dict[str, dict[str, Any]] = {}
        self._lock = threading.Lock()
        self._observer: Observer | None = None
        self._on_change_callbacks: list[Callable[[], None]] = []

    def load_from_directory(self, tools_dir: Path) -> None:
        """Load all tool definitions from a directory."""
        tools = load_all_tools(tools_dir)
        valid_tools: dict[str, dict[str, Any]] = {}
        for name, definition in tools.items():
            errors = validate_tool_definition(definition)
            if errors:
                logger.error("Tool '%s' has schema errors: %s", name, errors)
            else:
                valid_tools[name] = definition

        with self._lock:
            self._tools = valid_tools
        logger.info("Registry loaded %d tools", len(valid_tools))

    def get(self, name: str) -> dict[str, Any] | None:
        """Get a tool definition by name."""
        with self._lock:
            return self._tools.get(name)

    def list_tools(self) -> list[dict[str, Any]]:
        """Return all registered tool definitions."""
        with self._lock:
            return list(self._tools.values())

    def list_tool_names(self) -> list[str]:
        """Return all registered tool names."""
        with self._lock:
            return list(self._tools.keys())

    def register(self, definition: dict[str, Any]) -> None:
        """Register or update a single tool definition."""
        errors = validate_tool_definition(definition)
        if errors:
            raise ValueError(f"Invalid tool definition: {errors}")
        name = definition["name"]
        with self._lock:
            self._tools[name] = definition
        logger.info("Registered tool: %s", name)

    def unregister(self, name: str) -> bool:
        """Remove a tool from the registry. Returns True if it existed."""
        with self._lock:
            return self._tools.pop(name, None) is not None

    def on_change(self, callback: Callable[[], None]) -> None:
        """Register a callback to be invoked when the registry changes."""
        self._on_change_callbacks.append(callback)

    def _notify_change(self) -> None:
        for cb in self._on_change_callbacks:
            try:
                cb()
            except Exception:
                logger.exception("Error in registry change callback")

    def start_watching(self, tools_dir: Path) -> None:
        """Start watching the tools directory for changes."""
        if self._observer is not None:
            return

        handler = _ToolFileHandler(self, tools_dir)
        self._observer = Observer()
        self._observer.schedule(handler, str(tools_dir), recursive=False)
        self._observer.daemon = True
        self._observer.start()
        logger.info("Watching tools directory: %s", tools_dir)

    def stop_watching(self) -> None:
        """Stop watching the tools directory."""
        if self._observer is not None:
            self._observer.stop()
            self._observer.join(timeout=5)
            self._observer = None


class _ToolFileHandler(FileSystemEventHandler):
    """Watchdog handler that reloads tool definitions on file changes."""

    def __init__(self, registry: ToolRegistry, tools_dir: Path) -> None:
        self._registry = registry
        self._tools_dir = tools_dir

    def on_created(self, event: FileSystemEvent) -> None:
        self._handle(event)

    def on_modified(self, event: FileSystemEvent) -> None:
        self._handle(event)

    def on_deleted(self, event: FileSystemEvent) -> None:
        if event.is_directory:
            return
        path = Path(str(event.src_path))
        if path.suffix == ".json":
            name = path.stem.replace(".", ".")
            if self._registry.unregister(name):
                logger.info("Removed tool (file deleted): %s", name)
                self._registry._notify_change()

    def _handle(self, event: FileSystemEvent) -> None:
        if event.is_directory:
            return
        path = Path(str(event.src_path))
        if path.suffix != ".json":
            return
        try:
            definition = load_tool_file(path)
            self._registry.register(definition)
            self._registry._notify_change()
        except Exception:
            logger.exception("Failed to reload tool from %s", path.name)
