"""Tests for the LLM workflow reviewer."""

import json
import pytest
from unittest.mock import AsyncMock

from orchestrator.llm.base import BaseLLMProvider, LLMResponse, Message
from orchestrator.llm.workflow_reviewer import WorkflowReviewer


class FakeLLMProvider(BaseLLMProvider):
    """Fake LLM provider that returns pre-configured responses."""

    def __init__(self, response_content: str = ""):
        self._response_content = response_content

    @property
    def name(self) -> str:
        return "fake"

    async def chat(self, messages, tools=None, temperature=0.0):
        return LLMResponse(content=self._response_content)

    def format_tools(self, tool_definitions):
        return tool_definitions


class TestWorkflowReviewer:
    """Tests for WorkflowReviewer."""

    @pytest.fixture
    def sample_steps(self):
        return [
            {"tool_name": "revit.create_wall", "args": {"height": 10}},
            {"tool_name": "revit.get_element_info", "args": {"element_id": 123}},
        ]

    async def test_review_returns_parsed_result(self, sample_steps):
        response = json.dumps({
            "summary": "A workflow that creates a wall and inspects it.",
            "flags": ["Missing validation that element exists after creation"],
            "suggestions": [
                {"step_id": "step2", "suggestion": "Add guard to check step1 succeeded"}
            ],
        })

        provider = FakeLLMProvider(response)
        reviewer = WorkflowReviewer(provider)
        result = await reviewer.review(sample_steps, "Create and inspect wall")

        assert result["summary"] == "A workflow that creates a wall and inspects it."
        assert len(result["flags"]) == 1
        assert "Missing validation" in result["flags"][0]
        assert len(result["suggestions"]) == 1
        assert result["suggestions"][0]["step_id"] == "step2"

    async def test_review_handles_markdown_fences(self, sample_steps):
        response = '```json\n{"summary": "OK", "flags": [], "suggestions": []}\n```'

        provider = FakeLLMProvider(response)
        reviewer = WorkflowReviewer(provider)
        result = await reviewer.review(sample_steps, "Test workflow")

        assert result["summary"] == "OK"
        assert result["flags"] == []
        assert result["suggestions"] == []

    async def test_review_handles_malformed_json(self, sample_steps):
        provider = FakeLLMProvider("This is not valid JSON at all")
        reviewer = WorkflowReviewer(provider)
        result = await reviewer.review(sample_steps, "Test workflow")

        assert "This is not valid JSON" in result["summary"]
        assert "Could not parse LLM response as JSON" in result["flags"]
        assert result["suggestions"] == []

    async def test_review_handles_missing_keys(self, sample_steps):
        response = json.dumps({"summary": "Partial response"})

        provider = FakeLLMProvider(response)
        reviewer = WorkflowReviewer(provider)
        result = await reviewer.review(sample_steps, "Test workflow")

        assert result["summary"] == "Partial response"
        assert result["flags"] == []
        assert result["suggestions"] == []

    async def test_review_handles_provider_exception(self, sample_steps):
        provider = AsyncMock(spec=BaseLLMProvider)
        provider.chat.side_effect = Exception("API error")

        reviewer = WorkflowReviewer(provider)
        result = await reviewer.review(sample_steps, "Test workflow")

        assert "Review failed" in result["summary"]
        assert len(result["flags"]) == 1

    async def test_review_prompt_includes_steps(self, sample_steps):
        """Verify that the prompt sent to the LLM includes the step data."""
        provider = AsyncMock(spec=BaseLLMProvider)
        provider.chat.return_value = LLMResponse(
            content=json.dumps({"summary": "OK", "flags": [], "suggestions": []})
        )

        reviewer = WorkflowReviewer(provider)
        await reviewer.review(sample_steps, "My workflow description")

        # Check that the LLM was called with the right structure
        call_args = provider.chat.call_args
        messages = call_args.kwargs.get("messages") or call_args[0][0]
        assert len(messages) == 2
        assert messages[0].role == "system"
        assert messages[1].role == "user"
        assert "My workflow description" in messages[1].content
        assert "revit.create_wall" in messages[1].content

        # Check temperature
        temperature = call_args.kwargs.get("temperature", call_args[1].get("temperature") if len(call_args) > 1 else None)
        assert temperature == 0.2

    async def test_review_handles_non_dict_suggestions(self, sample_steps):
        """Suggestions that are not dicts should be filtered out."""
        response = json.dumps({
            "summary": "OK",
            "flags": [],
            "suggestions": ["not a dict", {"step_id": "step1", "suggestion": "good"}],
        })

        provider = FakeLLMProvider(response)
        reviewer = WorkflowReviewer(provider)
        result = await reviewer.review(sample_steps, "Test")

        assert len(result["suggestions"]) == 1
        assert result["suggestions"][0]["step_id"] == "step1"
