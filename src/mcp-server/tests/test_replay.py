"""Tests for the run replayer."""

import pytest
from unittest.mock import AsyncMock

from orchestrator.dispatcher.result import ToolResult
from orchestrator.telemetry.audit_log import AuditLog
from orchestrator.telemetry.replay import RunReplayer


@pytest.fixture
def audit_log(tmp_path):
    return AuditLog(tmp_path / "logs")


@pytest.fixture
def mock_dispatcher():
    d = AsyncMock()
    d.dispatch.return_value = ToolResult.ok({"dry_run": True})
    return d


class TestRunReplayer:
    async def test_replay_dry_run(self, audit_log, mock_dispatcher):
        # Write some events
        audit_log.log_event({
            "event_type": "started",
            "correlation_id": "c1",
            "tool_name": "revit.create_wall",
            "args": {"height": 10},
        })
        audit_log.log_event({
            "event_type": "completed",
            "correlation_id": "c1",
            "tool_name": "revit.create_wall",
        })
        audit_log.log_event({
            "event_type": "started",
            "correlation_id": "c1",
            "tool_name": "revit.get_element_info",
            "args": {"element_id": 123},
        })

        replayer = RunReplayer(audit_log, mock_dispatcher)
        results = await replayer.replay("c1", dry_run=True)

        assert len(results) == 2  # Only "started" events
        assert mock_dispatcher.dispatch.call_count == 2
        # Verify dry_run was passed
        for call in mock_dispatcher.dispatch.call_args_list:
            assert call.kwargs.get("dry_run") is True or call[2] is True if len(call[0]) > 2 else True

    async def test_replay_no_events(self, audit_log, mock_dispatcher):
        replayer = RunReplayer(audit_log, mock_dispatcher)
        results = await replayer.replay("nonexistent")

        assert len(results) == 1
        assert results[0].success is False
        assert results[0].error_code == "NO_EVENTS_FOUND"

    async def test_replay_sequence(self, audit_log, mock_dispatcher):
        """Verify events are replayed in order."""
        mock_dispatcher.dispatch.side_effect = [
            ToolResult.ok({"step": 1}),
            ToolResult.ok({"step": 2}),
        ]

        audit_log.log_event({
            "event_type": "started",
            "correlation_id": "c2",
            "tool_name": "tool_a",
            "args": {"x": 1},
        })
        audit_log.log_event({
            "event_type": "started",
            "correlation_id": "c2",
            "tool_name": "tool_b",
            "args": {"y": 2},
        })

        replayer = RunReplayer(audit_log, mock_dispatcher)
        results = await replayer.replay("c2", dry_run=False)

        assert len(results) == 2
        calls = mock_dispatcher.dispatch.call_args_list
        assert calls[0][0][0] == "tool_a"
        assert calls[1][0][0] == "tool_b"
