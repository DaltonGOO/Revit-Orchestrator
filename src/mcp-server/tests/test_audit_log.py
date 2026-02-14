"""Tests for the persistent audit log."""

import json
from datetime import datetime, timezone

import pytest

from orchestrator.telemetry.audit_log import AuditLog


@pytest.fixture
def audit_log(tmp_path):
    return AuditLog(tmp_path / "logs")


class TestAuditLog:
    def test_log_event_creates_file(self, audit_log):
        audit_log.log_event({"event_type": "test", "tool_name": "revit.test"})
        today = datetime.now(timezone.utc).strftime("%Y-%m-%d")
        log_path = audit_log.log_dir / f"audit-{today}.jsonl"
        assert log_path.exists()

    def test_log_event_appends_lines(self, audit_log):
        audit_log.log_event({"event_type": "first"})
        audit_log.log_event({"event_type": "second"})

        today = datetime.now(timezone.utc).strftime("%Y-%m-%d")
        log_path = audit_log.log_dir / f"audit-{today}.jsonl"
        lines = log_path.read_text().strip().split("\n")
        assert len(lines) == 2

    def test_read_events_returns_all(self, audit_log):
        audit_log.log_event({"event_type": "a", "tool_name": "t1"})
        audit_log.log_event({"event_type": "b", "tool_name": "t2"})

        events = audit_log.read_events()
        assert len(events) == 2

    def test_read_events_filter_by_correlation_id(self, audit_log):
        audit_log.log_event({"correlation_id": "c1", "tool_name": "t1"})
        audit_log.log_event({"correlation_id": "c2", "tool_name": "t2"})
        audit_log.log_event({"correlation_id": "c1", "tool_name": "t3"})

        events = audit_log.read_events(correlation_id="c1")
        assert len(events) == 2
        assert all(e["correlation_id"] == "c1" for e in events)

    def test_read_events_filter_by_tool_name(self, audit_log):
        audit_log.log_event({"tool_name": "revit.create_wall"})
        audit_log.log_event({"tool_name": "revit.get_element_info"})

        events = audit_log.read_events(tool_name="revit.create_wall")
        assert len(events) == 1

    def test_read_events_with_limit(self, audit_log):
        for i in range(10):
            audit_log.log_event({"event_type": f"event_{i}"})

        events = audit_log.read_events(limit=3)
        assert len(events) == 3

    def test_read_events_empty_file(self, audit_log):
        events = audit_log.read_events()
        assert events == []

    def test_event_gets_auto_id_and_timestamp(self, audit_log):
        audit_log.log_event({"event_type": "test"})
        events = audit_log.read_events()
        assert "event_id" in events[0]
        assert "timestamp" in events[0]

    def test_date_based_filename(self, audit_log):
        today = datetime.now(timezone.utc).strftime("%Y-%m-%d")
        audit_log.log_event({"event_type": "test"})
        log_path = audit_log.log_dir / f"audit-{today}.jsonl"
        assert log_path.exists()
