"""Integration tests for dispatcher safety features."""

import pytest
from unittest.mock import AsyncMock, MagicMock

from orchestrator.dispatcher.dispatcher import Dispatcher
from orchestrator.dispatcher.result import ToolResult
from orchestrator.registry.registry import ToolRegistry
from orchestrator.safety.approval import ApprovalGate


def _make_tool_approval():
    return {
        "name": "revit.write_tool",
        "adapter": "revit",
        "description": "A test write tool that requires approval for execution",
        "parameters": {
            "type": "object",
            "properties": {"arg1": {"type": "string"}},
            "required": ["arg1"],
        },
        "permissions": {"mode": "write", "approval_required": True},
        "side_effects": ["creates_elements"],
    }


class FakeApprovalConnection:
    def __init__(self, approved):
        self._approved = approved

    async def send_and_wait(self, message, timeout=120.0):
        return {"type": "approval_response", "payload": {"approved": self._approved}}


class TestDispatcherSafety:
    async def test_precondition_failure(self, tmp_path):
        reg = ToolRegistry()
        reg.register({
            "name": "revit.test_tool",
            "adapter": "revit",
            "description": "A test tool with preconditions for safety testing",
            "parameters": {"type": "object", "properties": {}},
            "preconditions": [{"check": "document_open"}],
        })

        checker = AsyncMock()
        checker.check.return_value = ["No document open"]

        dispatcher = Dispatcher(
            registry=reg,
            adapters={"revit": AsyncMock()},
            handlers_dir=tmp_path,
            precondition_checker=checker,
        )

        result = await dispatcher.dispatch("revit.test_tool", {})
        assert result.success is False
        assert result.error_code == "PRECONDITION_FAILED"

    async def test_approval_denied(self, tmp_path):
        reg = ToolRegistry()
        reg.register(_make_tool_approval())

        gate = ApprovalGate(FakeApprovalConnection(approved=False))

        dispatcher = Dispatcher(
            registry=reg,
            adapters={"revit": AsyncMock()},
            handlers_dir=tmp_path,
            approval_gate=gate,
        )

        result = await dispatcher.dispatch("revit.write_tool", {"arg1": "test"})
        assert result.success is False
        assert result.error_code == "USER_DENIED"

    async def test_approval_approved_then_executes(self, tmp_path):
        reg = ToolRegistry()
        reg.register(_make_tool_approval())

        gate = ApprovalGate(FakeApprovalConnection(approved=True))
        adapter = AsyncMock()
        adapter.execute.return_value = ToolResult.ok({"done": True})

        dispatcher = Dispatcher(
            registry=reg,
            adapters={"revit": adapter},
            handlers_dir=tmp_path,
            approval_gate=gate,
        )

        # We need a handler, so let's mock the handler loading
        import sys
        import types
        fake_mod = types.ModuleType("orchestrator.handlers.revit_write_tool")
        async def execute(args, **kwargs):
            return ToolResult.ok({"done": True})
        fake_mod.execute = execute
        sys.modules["orchestrator.handlers.revit_write_tool"] = fake_mod

        try:
            result = await dispatcher.dispatch("revit.write_tool", {"arg1": "test"})
            assert adapter.execute.called
        finally:
            sys.modules.pop("orchestrator.handlers.revit_write_tool", None)
