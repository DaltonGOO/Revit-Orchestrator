"""Tests for query history functionality."""

import pytest

from orchestrator.telemetry.audit_log import AuditLog


@pytest.fixture
def audit_log(tmp_path):
    return AuditLog(tmp_path / "logs")


class TestQueryHistory:
    def test_query_by_tool_name(self, audit_log):
        audit_log.log_event({"tool_name": "revit.create_wall", "event_type": "started"})
        audit_log.log_event({"tool_name": "revit.get_element_info", "event_type": "started"})
        audit_log.log_event({"tool_name": "revit.create_wall", "event_type": "completed"})

        events = audit_log.read_events(tool_name="revit.create_wall")
        assert len(events) == 2

    def test_query_by_correlation_id(self, audit_log):
        audit_log.log_event({"correlation_id": "abc", "tool_name": "t1"})
        audit_log.log_event({"correlation_id": "def", "tool_name": "t2"})
        audit_log.log_event({"correlation_id": "abc", "tool_name": "t3"})

        events = audit_log.read_events(correlation_id="abc")
        assert len(events) == 2

    def test_query_with_limit(self, audit_log):
        for i in range(20):
            audit_log.log_event({"tool_name": f"tool_{i}"})

        events = audit_log.read_events(limit=5)
        assert len(events) == 5

    def test_query_empty(self, audit_log):
        events = audit_log.read_events()
        assert events == []
