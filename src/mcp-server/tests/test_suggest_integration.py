"""End-to-end tests for workflow suggestion pipeline."""

import pytest
from unittest.mock import AsyncMock, MagicMock

from orchestrator.telemetry.audit_log import AuditLog
from orchestrator.telemetry.pattern_miner import PatternMiner
from orchestrator.telemetry.workflow_suggester import WorkflowSuggester


@pytest.fixture
def audit_log(tmp_path):
    return AuditLog(tmp_path / "logs")


class TestSuggestIntegration:
    async def test_end_to_end(self, audit_log):
        """Write events, mine patterns, suggest workflow."""
        # Write 4 repetitions of the same sequence
        for i in range(4):
            for tool in ["revit.create_wall", "revit.get_element_info"]:
                audit_log.log_event({
                    "event_type": "started",
                    "correlation_id": f"run_{i}",
                    "tool_name": tool,
                    "args": {"step": tool},
                })

        # Mine patterns
        miner = PatternMiner(audit_log)
        patterns = miner.find_patterns(min_occurrences=3)
        assert len(patterns) >= 1

        # Suggest workflow
        provider = AsyncMock()
        response = MagicMock()
        response.content = '{"name": "flow.wall_inspect", "description": "Create and inspect wall", "parameterize": []}'
        provider.chat.return_value = response

        suggester = WorkflowSuggester(provider)
        suggestion = await suggester.suggest(patterns[0])

        assert suggestion.name == "flow.wall_inspect"
        assert suggestion.workflow_def is not None
        steps = suggestion.workflow_def.get("workflow", {}).get("steps", [])
        assert len(steps) >= 2
