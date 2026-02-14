"""Tests for the workflow suggester."""

import pytest
from unittest.mock import AsyncMock, MagicMock

from orchestrator.telemetry.pattern_miner import DetectedPattern
from orchestrator.telemetry.workflow_suggester import WorkflowSuggester


@pytest.fixture
def pattern():
    return DetectedPattern(
        sequence=["revit.create_wall", "revit.get_element_info"],
        occurrences=5,
        example_args=[{"height": 10}, {"element_id": 123}],
        description="Create wall then inspect",
    )


class TestWorkflowSuggester:
    async def test_suggest_with_mock_llm(self, pattern):
        """Test that the prompt is structured correctly."""
        provider = AsyncMock()
        response = MagicMock()
        response.content = '{"name": "flow.create_and_inspect", "description": "Creates a wall and inspects it", "parameterize": ["height"]}'
        provider.chat.return_value = response

        suggester = WorkflowSuggester(provider)
        suggestion = await suggester.suggest(pattern)

        assert suggestion.name == "flow.create_and_inspect"
        assert suggestion.description == "Creates a wall and inspects it"
        assert "height" in suggestion.parameterize
        assert provider.chat.called

    async def test_suggest_prompt_structure(self, pattern):
        """Verify the prompt contains expected information."""
        provider = AsyncMock()
        response = MagicMock()
        response.content = '{"name": "flow.test", "description": "test", "parameterize": []}'
        provider.chat.return_value = response

        suggester = WorkflowSuggester(provider)
        await suggester.suggest(pattern)

        # Check the prompt was sent
        call_args = provider.chat.call_args
        messages = call_args.kwargs.get("messages") or call_args[0][0]
        prompt = messages[0].content
        assert "revit.create_wall" in prompt
        assert "revit.get_element_info" in prompt
        assert "5" in prompt  # occurrences

    async def test_fallback_on_parse_error(self, pattern):
        """When LLM returns invalid JSON, fallback should work."""
        provider = AsyncMock()
        response = MagicMock()
        response.content = "This is not JSON at all"
        provider.chat.return_value = response

        suggester = WorkflowSuggester(provider)
        suggestion = await suggester.suggest(pattern)

        # Should use auto-generated name
        assert suggestion.name.startswith("flow.")
        assert suggestion.workflow_def is not None

    async def test_workflow_def_has_steps(self, pattern):
        """The generated workflow_def should have valid steps."""
        provider = AsyncMock()
        response = MagicMock()
        response.content = '{"name": "flow.test", "description": "test", "parameterize": []}'
        provider.chat.return_value = response

        suggester = WorkflowSuggester(provider)
        suggestion = await suggester.suggest(pattern)

        steps = suggestion.workflow_def.get("workflow", {}).get("steps", [])
        assert len(steps) == 2
        assert steps[0]["tool"] == "revit.create_wall"
        assert steps[1]["tool"] == "revit.get_element_info"
