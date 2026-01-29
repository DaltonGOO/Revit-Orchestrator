"""Configuration settings for the Revit Orchestrator MCP server."""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path


@dataclass
class Config:
    """Server configuration loaded from environment variables."""

    pipe_name: str = r"\\.\pipe\RevitOrchestrator"
    tools_dir: Path = field(default_factory=lambda: Path(__file__).parent / "tools")
    handlers_dir: Path = field(default_factory=lambda: Path(__file__).parent / "handlers")

    # LLM settings
    llm_provider: str = "claude"  # "claude" or "openai"
    anthropic_api_key: str = ""
    anthropic_model: str = "claude-sonnet-4-20250514"
    openai_api_key: str = ""
    openai_model: str = "gpt-4o"

    # Pipe settings
    pipe_timeout_seconds: float = 30.0
    ping_interval_seconds: float = 30.0
    ping_timeout_seconds: float = 10.0

    # Hot-reload
    watch_tools_dir: bool = True

    @classmethod
    def from_env(cls) -> Config:
        """Load configuration from environment variables."""
        return cls(
            pipe_name=os.getenv("ORCHESTRATOR_PIPE_NAME", cls.pipe_name),
            tools_dir=Path(os.getenv("ORCHESTRATOR_TOOLS_DIR", str(cls.tools_dir.fget(None)))),  # type: ignore[union-attr]
            llm_provider=os.getenv("ORCHESTRATOR_LLM_PROVIDER", cls.llm_provider),
            anthropic_api_key=os.getenv("ANTHROPIC_API_KEY", ""),
            anthropic_model=os.getenv("ANTHROPIC_MODEL", cls.anthropic_model),
            openai_api_key=os.getenv("OPENAI_API_KEY", ""),
            openai_model=os.getenv("OPENAI_MODEL", cls.openai_model),
            pipe_timeout_seconds=float(
                os.getenv("ORCHESTRATOR_PIPE_TIMEOUT", str(cls.pipe_timeout_seconds))
            ),
            watch_tools_dir=os.getenv("ORCHESTRATOR_WATCH_TOOLS", "true").lower() == "true",
        )

    @classmethod
    def defaults(cls) -> Config:
        """Return a Config with all defaults (useful for testing)."""
        return cls(
            tools_dir=Path(__file__).parent / "tools",
            handlers_dir=Path(__file__).parent / "handlers",
        )
