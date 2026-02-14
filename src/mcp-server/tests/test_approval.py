"""Tests for the approval gate."""

import pytest
from unittest.mock import AsyncMock

from orchestrator.safety.approval import ApprovalGate


class FakeConnection:
    def __init__(self, approved):
        self._approved = approved

    async def send_and_wait(self, message, timeout=120.0):
        return {"type": "approval_response", "payload": {"approved": self._approved}}


class TestApprovalGate:
    def test_needs_approval_true(self):
        gate = ApprovalGate()
        defn = {"permissions": {"approval_required": True}}
        assert gate.needs_approval(defn) is True

    def test_needs_approval_false(self):
        gate = ApprovalGate()
        defn = {"permissions": {"approval_required": False}}
        assert gate.needs_approval(defn) is False

    def test_needs_approval_missing(self):
        gate = ApprovalGate()
        assert gate.needs_approval({}) is False

    async def test_request_approval_approved(self):
        gate = ApprovalGate(FakeConnection(approved=True))
        result = await gate.request_approval("revit.create_wall", {}, [])
        assert result is True

    async def test_request_approval_denied(self):
        gate = ApprovalGate(FakeConnection(approved=False))
        result = await gate.request_approval("revit.create_wall", {}, [])
        assert result is False

    async def test_no_connection_auto_approves(self):
        gate = ApprovalGate()  # No connection
        result = await gate.request_approval("revit.create_wall", {}, [])
        assert result is True
