"""LLM router — manages tool selection and argument building via LLM."""

from __future__ import annotations

import logging
from typing import Any

from .base import BaseLLMProvider, LLMResponse, Message
from ..registry.registry import ToolRegistry

logger = logging.getLogger(__name__)

SYSTEM_PROMPT = """You are Revit Orchestrator, an AI assistant that helps users work with Autodesk Revit.
You have access to tools that can read and modify Revit models. Use them when the user asks you to
perform operations on their model.

When using tools:
- Always confirm destructive operations before executing them.
- Provide clear descriptions of what you're doing and why.
- If a tool call fails, explain the error and suggest alternatives.
- Use the most specific tool available for the task.
"""


class LLMRouter:
    """Routes user messages through an LLM provider with tool definitions.

    The router:
    1. Builds the tool list from the registry
    2. Sends the conversation to the LLM with available tools
    3. Returns the LLM response (which may include tool calls)
    """

    def __init__(
        self,
        provider: BaseLLMProvider,
        registry: ToolRegistry,
        system_prompt: str = SYSTEM_PROMPT,
    ) -> None:
        self._provider = provider
        self._registry = registry
        self._system_prompt = system_prompt
        self._tools_cache: list[dict[str, Any]] | None = None

        # Invalidate cache when registry changes
        registry.on_change(self._invalidate_tools_cache)

    def _invalidate_tools_cache(self) -> None:
        self._tools_cache = None

    def _get_formatted_tools(self) -> list[dict[str, Any]]:
        """Get tool definitions formatted for the current provider."""
        if self._tools_cache is None:
            definitions = self._registry.list_tools()
            self._tools_cache = self._provider.format_tools(definitions)
        return self._tools_cache

    async def chat(
        self,
        messages: list[Message],
        temperature: float = 0.0,
    ) -> LLMResponse:
        """Send a conversation to the LLM with available tools.

        Prepends the system prompt and includes all registered tools.
        """
        full_messages = [Message(role="system", content=self._system_prompt)]
        full_messages.extend(messages)

        tools = self._get_formatted_tools()
        return await self._provider.chat(
            full_messages,
            tools=tools if tools else None,
            temperature=temperature,
        )
