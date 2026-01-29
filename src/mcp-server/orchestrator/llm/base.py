"""Abstract LLM provider interface."""

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Any


@dataclass
class LLMToolCall:
    """Represents a tool call requested by the LLM."""
    id: str
    name: str
    arguments: dict[str, Any]


@dataclass
class LLMResponse:
    """Response from an LLM provider."""
    content: str = ""
    tool_calls: list[LLMToolCall] = field(default_factory=list)
    finish_reason: str = "stop"
    usage: dict[str, int] = field(default_factory=dict)

    @property
    def has_tool_calls(self) -> bool:
        return len(self.tool_calls) > 0


@dataclass
class Message:
    """A chat message."""
    role: str  # "user", "assistant", "system", "tool"
    content: str
    tool_call_id: str | None = None
    tool_calls: list[LLMToolCall] | None = None


class BaseLLMProvider(ABC):
    """Abstract base class for LLM providers."""

    @property
    @abstractmethod
    def name(self) -> str:
        """Provider name identifier."""
        ...

    @abstractmethod
    async def chat(
        self,
        messages: list[Message],
        tools: list[dict[str, Any]] | None = None,
        temperature: float = 0.0,
    ) -> LLMResponse:
        """Send a chat completion request.

        Args:
            messages: Conversation history.
            tools: Optional list of tool definitions in provider-native format.
            temperature: Sampling temperature.

        Returns:
            LLMResponse with content and/or tool calls.
        """
        ...

    @abstractmethod
    def format_tools(self, tool_definitions: list[dict[str, Any]]) -> list[dict[str, Any]]:
        """Convert internal tool definitions to provider-specific format.

        Args:
            tool_definitions: Tool definitions from the registry.

        Returns:
            Provider-formatted tool list.
        """
        ...
