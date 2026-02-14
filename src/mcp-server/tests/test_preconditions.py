"""Tests for precondition checking."""

import pytest
from unittest.mock import AsyncMock

from orchestrator.safety.preconditions import PreconditionChecker


class FakeConnection:
    def __init__(self, status_payload):
        self._status = status_payload

    async def send_and_wait(self, message, timeout=10.0):
        return {"type": "status_response", "payload": self._status}


class TestPreconditionChecker:
    async def test_no_preconditions_passes(self):
        checker = PreconditionChecker()
        failures = await checker.check({"preconditions": []})
        assert failures == []

    async def test_missing_preconditions_passes(self):
        checker = PreconditionChecker()
        failures = await checker.check({})
        assert failures == []

    async def test_document_open_passes(self):
        conn = FakeConnection({"document_title": "Project.rvt"})
        checker = PreconditionChecker(conn)
        failures = await checker.check({
            "preconditions": [{"check": "document_open"}]
        })
        assert failures == []

    async def test_document_open_fails(self):
        conn = FakeConnection({})
        checker = PreconditionChecker(conn)
        failures = await checker.check({
            "preconditions": [{"check": "document_open"}]
        })
        assert len(failures) == 1
        assert "document" in failures[0].lower()

    async def test_revit_version_passes(self):
        conn = FakeConnection({"revit_version": "2025"})
        checker = PreconditionChecker(conn)
        failures = await checker.check({
            "preconditions": [{"check": "revit_version_gte", "value": "2024"}]
        })
        assert failures == []

    async def test_revit_version_fails(self):
        conn = FakeConnection({"revit_version": "2023"})
        checker = PreconditionChecker(conn)
        failures = await checker.check({
            "preconditions": [{"check": "revit_version_gte", "value": "2024"}]
        })
        assert len(failures) == 1

    async def test_worksharing_passes(self):
        conn = FakeConnection({"is_worksharing": True})
        checker = PreconditionChecker(conn)
        failures = await checker.check({
            "preconditions": [{"check": "worksharing_enabled"}]
        })
        assert failures == []

    async def test_worksharing_fails(self):
        conn = FakeConnection({"is_worksharing": False})
        checker = PreconditionChecker(conn)
        failures = await checker.check({
            "preconditions": [{"check": "worksharing_enabled"}]
        })
        assert len(failures) == 1
