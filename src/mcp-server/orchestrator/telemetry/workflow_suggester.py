"""Uses LLM to name and describe detected workflow patterns."""

from __future__ import annotations

import json
import logging
from dataclasses import dataclass, field
from typing import Any

from .pattern_miner import DetectedPattern

logger = logging.getLogger(__name__)


@dataclass
class WorkflowSuggestion:
    """A suggested workflow generated from a detected pattern."""

    name: str
    description: str
    parameterize: list[str]
    workflow_def: dict[str, Any]


class WorkflowSuggester:
    """Uses an LLM to name and describe detected patterns.

    Takes a DetectedPattern and asks the LLM to:
    - Name the workflow
    - Describe what it does
    - Identify which args should be parameterized
    """

    def __init__(self, llm_provider: Any) -> None:
        self._provider = llm_provider

    async def suggest(self, pattern: DetectedPattern) -> WorkflowSuggestion:
        """Generate a workflow suggestion from a detected pattern.

        Args:
            pattern: The detected recurring pattern.

        Returns:
            WorkflowSuggestion with name, description, and workflow definition.
        """
        prompt = self._build_prompt(pattern)

        try:
            from ..llm.base import Message

            messages = [Message(role="user", content=prompt)]
            response = await self._provider.chat(
                messages=messages,
                tools=[],
                temperature=0.3,
            )

            return self._parse_response(response.content, pattern)
        except Exception:
            logger.exception("Failed to generate suggestion for pattern")
            return self._fallback_suggestion(pattern)

    def _build_prompt(self, pattern: DetectedPattern) -> str:
        return (
            "You are a Revit automation assistant. A recurring pattern of tool calls "
            "has been detected in the user's workflow history.\n\n"
            f"Tool sequence: {' → '.join(pattern.sequence)}\n"
            f"Occurrences: {pattern.occurrences}\n"
            f"Example arguments:\n{json.dumps(pattern.example_args, indent=2)}\n\n"
            "Please respond with a JSON object containing:\n"
            '- "name": a short, descriptive workflow name (lowercase with dots, e.g., "flow.setup_room")\n'
            '- "description": a one-line description of what this workflow does\n'
            '- "parameterize": a list of argument keys that should become workflow parameters\n\n'
            "Respond ONLY with the JSON object, no other text."
        )

    def _parse_response(
        self, content: str, pattern: DetectedPattern
    ) -> WorkflowSuggestion:
        try:
            # Try to extract JSON from the response
            start = content.index("{")
            end = content.rindex("}") + 1
            data = json.loads(content[start:end])

            name = data.get("name", self._auto_name(pattern))
            description = data.get("description", pattern.description)
            parameterize = data.get("parameterize", [])

            workflow_def = self._build_workflow_def(
                name, description, pattern, parameterize
            )

            return WorkflowSuggestion(
                name=name,
                description=description,
                parameterize=parameterize,
                workflow_def=workflow_def,
            )
        except (json.JSONDecodeError, ValueError):
            logger.warning("Failed to parse LLM response, using fallback")
            return self._fallback_suggestion(pattern)

    def _fallback_suggestion(self, pattern: DetectedPattern) -> WorkflowSuggestion:
        name = self._auto_name(pattern)
        description = pattern.description

        workflow_def = self._build_workflow_def(name, description, pattern, [])

        return WorkflowSuggestion(
            name=name,
            description=description,
            parameterize=[],
            workflow_def=workflow_def,
        )

    @staticmethod
    def _auto_name(pattern: DetectedPattern) -> str:
        """Generate an automatic name from the tool sequence."""
        if not pattern.sequence:
            return "flow.unnamed_workflow"
        # Use last tool name's action part
        parts = [t.split(".")[-1] for t in pattern.sequence]
        return f"flow.auto_{'_and_'.join(parts[:2])}"

    @staticmethod
    def _build_workflow_def(
        name: str,
        description: str,
        pattern: DetectedPattern,
        parameterize: list[str],
    ) -> dict[str, Any]:
        """Build a workflow definition from a pattern."""
        steps = []
        for i, (tool, args) in enumerate(
            zip(pattern.sequence, pattern.example_args)
        ):
            step: dict[str, Any] = {
                "id": f"step{i + 1}",
                "tool": tool,
                "args": {k: v for k, v in args.items() if k not in parameterize},
            }
            steps.append(step)

        # Build parameters from parameterized args
        properties: dict[str, Any] = {}
        for key in parameterize:
            properties[key] = {"type": "string", "description": f"Parameter: {key}"}

        return {
            "name": name,
            "adapter": "workflow",
            "description": description,
            "parameters": {
                "type": "object",
                "properties": properties,
            },
            "workflow": {"steps": steps},
        }
