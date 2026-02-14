"""Tests for the pattern miner."""

import pytest

from orchestrator.telemetry.audit_log import AuditLog
from orchestrator.telemetry.pattern_miner import PatternMiner


@pytest.fixture
def audit_log(tmp_path):
    return AuditLog(tmp_path / "logs")


def _write_run(audit_log, correlation_id, tools):
    """Write a sequence of tool started events."""
    for i, tool in enumerate(tools):
        audit_log.log_event({
            "event_type": "started",
            "correlation_id": correlation_id,
            "tool_name": tool,
            "args": {"step": i},
            "sequence": i + 1,
        })


class TestPatternMiner:
    def test_detects_repeated_pattern(self, audit_log):
        """A 2-tool sequence repeated 3 times should be detected."""
        for i in range(3):
            _write_run(audit_log, f"run{i}", ["revit.create_wall", "revit.get_element_info"])

        miner = PatternMiner(audit_log)
        patterns = miner.find_patterns(min_occurrences=3)

        assert len(patterns) >= 1
        found = any(
            p.sequence == ["revit.create_wall", "revit.get_element_info"]
            for p in patterns
        )
        assert found

    def test_min_occurrences_filter(self, audit_log):
        """Patterns below min_occurrences should be filtered out."""
        for i in range(2):
            _write_run(audit_log, f"run{i}", ["tool_a", "tool_b"])

        miner = PatternMiner(audit_log)
        patterns = miner.find_patterns(min_occurrences=3)
        assert len(patterns) == 0

    def test_no_events_returns_empty(self, audit_log):
        miner = PatternMiner(audit_log)
        patterns = miner.find_patterns()
        assert patterns == []

    def test_pattern_has_example_args(self, audit_log):
        for i in range(3):
            _write_run(audit_log, f"run{i}", ["tool_a", "tool_b"])

        miner = PatternMiner(audit_log)
        patterns = miner.find_patterns(min_occurrences=3)

        assert len(patterns) >= 1
        assert len(patterns[0].example_args) > 0

    def test_longer_pattern(self, audit_log):
        """3-tool patterns should also be detected."""
        for i in range(4):
            _write_run(audit_log, f"run{i}", ["tool_a", "tool_b", "tool_c"])

        miner = PatternMiner(audit_log)
        patterns = miner.find_patterns(min_occurrences=3, min_length=3)

        found = any(
            p.sequence == ["tool_a", "tool_b", "tool_c"]
            for p in patterns
        )
        assert found
