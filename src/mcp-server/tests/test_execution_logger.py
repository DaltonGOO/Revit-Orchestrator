"""Tests for the execution logger with audit log integration."""

import pytest

from orchestrator.dispatcher.result import ToolResult
from orchestrator.execution_logger import ExecutionLogger
from orchestrator.telemetry.audit_log import AuditLog


class FakePipeConnection:
    """Fake pipe connection that captures sent messages."""

    def __init__(self):
        self.sent_messages: list[dict] = []

    async def send(self, message: dict) -> None:
        self.sent_messages.append(message)


@pytest.fixture
def fake_connection():
    return FakePipeConnection()


@pytest.fixture
def audit_log(tmp_path):
    return AuditLog(tmp_path / "logs")


class TestExecutionLogger:
    async def test_log_started_sends_to_pipe(self, fake_connection):
        logger = ExecutionLogger(fake_connection)
        logger.start_correlation()
        event_id = await logger.log_started("revit.test", {"arg": "val"})

        assert len(fake_connection.sent_messages) == 1
        msg = fake_connection.sent_messages[0]
        assert msg["type"] == "execution_event"
        assert msg["payload"]["event_type"] == "started"
        assert event_id

    async def test_log_completed_sends_to_pipe(self, fake_connection):
        logger = ExecutionLogger(fake_connection)
        logger.start_correlation()
        event_id = await logger.log_started("revit.test", {})
        result = ToolResult.ok({"element_id": 123})
        await logger.log_completed(event_id, result, "revit.test", {})

        assert len(fake_connection.sent_messages) == 2
        completed_msg = fake_connection.sent_messages[1]
        assert completed_msg["payload"]["event_type"] == "completed"

    async def test_log_writes_to_audit_log(self, fake_connection, audit_log):
        logger = ExecutionLogger(fake_connection, audit_log=audit_log)
        logger.start_correlation()
        event_id = await logger.log_started("revit.test", {"x": 1})
        result = ToolResult.ok({"element_id": 123})
        await logger.log_completed(event_id, result, "revit.test", {"x": 1})

        events = audit_log.read_events()
        assert len(events) == 2
        assert events[0]["event_type"] == "started"
        assert events[1]["event_type"] == "completed"
        assert events[0]["args_hash"]  # Should have hash

    async def test_log_failed_writes_to_audit_log(self, fake_connection, audit_log):
        logger = ExecutionLogger(fake_connection, audit_log=audit_log)
        logger.start_correlation()
        event_id = await logger.log_started("revit.test", {})
        await logger.log_failed(event_id, "Something broke", "revit.test", {})

        events = audit_log.read_events()
        assert len(events) == 2
        assert events[1]["event_type"] == "failed"
        assert events[1]["error"] == "Something broke"

    async def test_correlation_summary(self, fake_connection, audit_log):
        logger = ExecutionLogger(fake_connection, audit_log=audit_log)
        cid = logger.start_correlation()
        logger.log_correlation_summary(cid, total_usage={"input_tokens": 100})

        events = audit_log.read_events()
        assert len(events) == 1
        assert events[0]["event_type"] == "correlation_summary"

    async def test_hash_args_deterministic(self, fake_connection):
        logger = ExecutionLogger(fake_connection)
        h1 = logger._hash_args({"a": 1, "b": 2})
        h2 = logger._hash_args({"b": 2, "a": 1})  # Same keys, different order
        assert h1 == h2
        assert len(h1) == 16
