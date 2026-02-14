"""Shared fixtures for Revit Orchestrator tests."""

import pathlib

import pytest

from orchestrator.config import Config
from orchestrator.registry.registry import ToolRegistry


FIXTURES_DIR = pathlib.Path(__file__).parent / "fixtures"
TOOLS_DIR = FIXTURES_DIR / "tools"


@pytest.fixture
def mock_config():
    """Return a Config with defaults but tools_dir pointing to test fixtures."""
    return Config(tools_dir=TOOLS_DIR)


@pytest.fixture
def mock_registry():
    """Create a ToolRegistry and load definitions from the fixtures/tools/ directory."""
    reg = ToolRegistry()
    reg.load_from_directory(TOOLS_DIR)
    return reg


class FakePipeConnection:
    """A fake pipe connection that captures sent messages into a list."""

    def __init__(self):
        self.messages: list = []

    async def send(self, msg):
        self.messages.append(msg)


@pytest.fixture
def fake_pipe_connection():
    """Return a FakePipeConnection instance that captures sent messages."""
    return FakePipeConnection()
