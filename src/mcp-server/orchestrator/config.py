"""Configuration settings for the Revit Orchestrator MCP server."""

from __future__ import annotations

import json
import logging
import os
from dataclasses import dataclass, field
from pathlib import Path

logger = logging.getLogger(__name__)

# Anchor to the ``src/mcp-server/`` directory (parent of the ``orchestrator`` package).
_PROJECT_DIR = Path(__file__).resolve().parent.parent


# Keys that are persisted to / loaded from the settings file.
PERSISTED_KEYS = frozenset({
    "llm_provider",
    "anthropic_api_key",
    "anthropic_model",
    "openai_api_key",
    "openai_model",
    "openai_base_url",
})


@dataclass
class Config:
    """Server configuration loaded from environment variables."""

    pipe_name: str = r"\\.\pipe\RevitOrchestrator"
    tools_dir: Path = field(default_factory=lambda: Path(__file__).parent / "tools")
    handlers_dir: Path = field(default_factory=lambda: Path(__file__).parent / "handlers")

    # LLM settings
    llm_provider: str = "claude"  # "claude", "openai", or "openai-compatible"
    anthropic_api_key: str = ""
    anthropic_model: str = "claude-sonnet-4-20250514"
    openai_api_key: str = ""
    openai_model: str = "gpt-4o"
    openai_base_url: str = ""

    # Settings persistence
    settings_file: Path = field(default_factory=lambda: Path(
        os.environ.get("LOCALAPPDATA", os.path.expanduser("~")),
        "RevitOrchestrator",
        "settings.json",
    ))

    # Pipe settings
    pipe_timeout_seconds: float = 30.0
    ping_interval_seconds: float = 30.0
    ping_timeout_seconds: float = 10.0

    # Hot-reload
    watch_tools_dir: bool = True

    # Audit logging
    audit_log_dir: Path = field(default_factory=lambda: _PROJECT_DIR / "logs")

    # Event store (SQLite)
    event_store_path: Path = field(default_factory=lambda: _PROJECT_DIR / "data" / "orchestrator.db")
    blob_store_dir: Path = field(default_factory=lambda: _PROJECT_DIR / "data" / "blobs")
    migrate_jsonl_on_startup: bool = True

    # Embeddings
    embedding_model: str = "all-MiniLM-L6-v2"

    # Logging
    log_dir: Path = field(default_factory=lambda: Path(
        os.environ.get("LOCALAPPDATA", os.path.expanduser("~")),
        "RevitOrchestrator",
        "logs",
    ))

    def save_to_file(self) -> None:
        """Write persisted settings to the JSON file."""
        data = {k: getattr(self, k) for k in PERSISTED_KEYS}
        self.settings_file.parent.mkdir(parents=True, exist_ok=True)
        with open(self.settings_file, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
        logger.info("Settings saved to %s", self.settings_file)

    @classmethod
    def load_from_file(cls, path: Path | None = None) -> dict[str, str]:
        """Read persisted settings from JSON if the file exists."""
        if path is None:
            path = cls().settings_file
        if not path.exists():
            return {}
        try:
            with open(path, "r", encoding="utf-8") as f:
                data = json.load(f)
            # Only return keys we recognise
            return {k: v for k, v in data.items() if k in PERSISTED_KEYS and v}
        except Exception:
            logger.warning("Failed to load settings from %s", path, exc_info=True)
            return {}

    def apply_update(self, updates: dict[str, str]) -> None:
        """Validate and apply a settings update, then persist to file."""
        for key, value in updates.items():
            if key in PERSISTED_KEYS and hasattr(self, key):
                setattr(self, key, value)
        self.save_to_file()

    @classmethod
    def from_env(cls) -> Config:
        """Load configuration from environment variables, then overlay persisted file settings."""
        defaults = cls()
        cfg = cls(
            pipe_name=os.getenv("ORCHESTRATOR_PIPE_NAME", defaults.pipe_name),
            tools_dir=Path(os.getenv("ORCHESTRATOR_TOOLS_DIR", str(defaults.tools_dir))),
            handlers_dir=Path(os.getenv("ORCHESTRATOR_HANDLERS_DIR", str(defaults.handlers_dir))),
            llm_provider=os.getenv("ORCHESTRATOR_LLM_PROVIDER", defaults.llm_provider),
            anthropic_api_key=os.getenv("ANTHROPIC_API_KEY", ""),
            anthropic_model=os.getenv("ANTHROPIC_MODEL", defaults.anthropic_model),
            openai_api_key=os.getenv("OPENAI_API_KEY", ""),
            openai_model=os.getenv("OPENAI_MODEL", defaults.openai_model),
            openai_base_url=os.getenv("OPENAI_BASE_URL", ""),
            pipe_timeout_seconds=float(
                os.getenv("ORCHESTRATOR_PIPE_TIMEOUT", str(defaults.pipe_timeout_seconds))
            ),
            watch_tools_dir=os.getenv("ORCHESTRATOR_WATCH_TOOLS", "true").lower() == "true",
            audit_log_dir=Path(
                os.getenv("ORCHESTRATOR_AUDIT_LOG_DIR", str(defaults.audit_log_dir))
            ),
            event_store_path=Path(
                os.getenv("ORCHESTRATOR_EVENT_STORE_PATH", str(defaults.event_store_path))
            ),
            blob_store_dir=Path(
                os.getenv("ORCHESTRATOR_BLOB_STORE_DIR", str(defaults.blob_store_dir))
            ),
            migrate_jsonl_on_startup=os.getenv(
                "ORCHESTRATOR_MIGRATE_JSONL", "true"
            ).lower() == "true",
            embedding_model=os.getenv(
                "ORCHESTRATOR_EMBEDDING_MODEL", defaults.embedding_model
            ),
            log_dir=Path(
                os.getenv("ORCHESTRATOR_LOG_DIR", str(defaults.log_dir))
            ),
        )
        # Overlay persisted settings (user explicitly chose them → they win)
        file_settings = cls.load_from_file(cfg.settings_file)
        for key, value in file_settings.items():
            if hasattr(cfg, key):
                setattr(cfg, key, value)
        return cfg

    @classmethod
    def defaults(cls) -> Config:
        """Return a Config with all defaults (useful for testing)."""
        return cls(
            tools_dir=Path(__file__).parent / "tools",
            handlers_dir=Path(__file__).parent / "handlers",
        )
