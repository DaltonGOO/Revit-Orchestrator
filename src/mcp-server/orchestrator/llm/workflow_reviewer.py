"""LLM-powered workflow review before saving."""

from __future__ import annotations

import json
import logging
from typing import Any

from .base import BaseLLMProvider, Message

logger = logging.getLogger(__name__)

REVIEW_SYSTEM_PROMPT = """\
You are a workflow quality reviewer for Revit Orchestrator, a system that \
automates Autodesk Revit operations through declarative workflows.

Analyze the provided workflow and return a JSON object with exactly these keys:
- "summary": A 1-2 sentence summary of what the workflow does and its overall quality.
- "flags": A list of strings, each describing an issue or concern found. \
  If no issues, return an empty list.
- "suggestions": A list of objects, each with "step_id" (string) and \
  "suggestion" (string) for step-specific improvements. If none, return an empty list.

Look for these categories of issues:
1. Missing validation steps (e.g., element exists before modifying it)
2. Missing failure handling (steps without on_failure or guards)
3. Potential side-effect conflicts between steps
4. Ordering issues (steps that should run before others)
5. Missing preconditions or postconditions

Return ONLY valid JSON. No markdown fences, no commentary outside the JSON object."""


class WorkflowReviewer:
    """Reviews a workflow definition using an LLM provider."""

    def __init__(self, provider: BaseLLMProvider) -> None:
        self._provider = provider

    async def review(self, steps: list[dict[str, Any]], description: str) -> dict[str, Any]:
        """Review a workflow and return summary, flags, and suggestions.

        Args:
            steps: List of workflow step dicts (tool_name, args, etc.)
            description: Human-readable description of the workflow.

        Returns:
            Dict with "summary", "flags", and "suggestions" keys.
        """
        steps_text = json.dumps(steps, indent=2, default=str)
        user_content = (
            f"Workflow description: {description}\n\n"
            f"Steps:\n{steps_text}\n\n"
            "Please review this workflow for quality, safety, and completeness."
        )

        messages = [
            Message(role="system", content=REVIEW_SYSTEM_PROMPT),
            Message(role="user", content=user_content),
        ]

        try:
            response = await self._provider.chat(
                messages=messages,
                tools=None,
                temperature=0.2,
            )

            return self._parse_response(response.content)
        except Exception as e:
            logger.exception("LLM review failed")
            return {
                "summary": f"Review failed: {e}",
                "flags": ["LLM review encountered an error"],
                "suggestions": [],
            }

    @staticmethod
    def _parse_response(content: str) -> dict[str, Any]:
        """Parse the LLM response JSON, handling malformed output gracefully."""
        # Strip markdown fences if present
        text = content.strip()
        if text.startswith("```"):
            lines = text.split("\n")
            # Remove first and last lines (fences)
            lines = lines[1:]
            if lines and lines[-1].strip() == "```":
                lines = lines[:-1]
            text = "\n".join(lines).strip()

        try:
            result = json.loads(text)
        except json.JSONDecodeError:
            return {
                "summary": content[:500],
                "flags": ["Could not parse LLM response as JSON"],
                "suggestions": [],
            }

        # Ensure required keys exist with correct types
        return {
            "summary": str(result.get("summary", "")),
            "flags": [str(f) for f in result.get("flags", [])],
            "suggestions": [
                {
                    "step_id": str(s.get("step_id", "")),
                    "suggestion": str(s.get("suggestion", "")),
                }
                for s in result.get("suggestions", [])
                if isinstance(s, dict)
            ],
        }
