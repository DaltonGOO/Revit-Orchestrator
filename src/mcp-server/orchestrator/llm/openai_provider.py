"""OpenAI LLM provider implementation."""

from __future__ import annotations

import json
import logging
import re
import uuid
from typing import Any

import openai

from .base import BaseLLMProvider, LLMResponse, LLMToolCall, Message

logger = logging.getLogger(__name__)


class OpenAIProvider(BaseLLMProvider):
    """OpenAI provider using the OpenAI SDK."""

    def __init__(self, api_key: str, model: str = "gpt-4o", base_url: str | None = None) -> None:
        kwargs: dict[str, Any] = {"api_key": api_key}
        if base_url:
            kwargs["base_url"] = base_url
        self._client = openai.AsyncOpenAI(**kwargs)
        self._model = model
        self._is_local = base_url is not None

    @property
    def name(self) -> str:
        return "openai"

    async def chat(
        self,
        messages: list[Message],
        tools: list[dict[str, Any]] | None = None,
        temperature: float = 0.0,
    ) -> LLMResponse:
        """Send a chat request to OpenAI."""
        if self._is_local:
            # For local models: fold system messages, embed tools as text,
            # and flatten to user/assistant roles only.
            messages = self._fold_system_messages(messages)
            if tools:
                messages = self._embed_tools_in_prompt(messages, tools)
            messages = self._flatten_for_local(messages)
            tools = None  # Don't pass tools API parameter to local models

        api_messages = []
        for msg in messages:
            if msg.role == "tool":
                api_messages.append({
                    "role": "tool",
                    "tool_call_id": msg.tool_call_id,
                    "content": msg.content,
                })
            elif msg.role == "assistant" and msg.tool_calls:
                api_msg: dict[str, Any] = {
                    "role": "assistant",
                    "content": msg.content or None,
                    "tool_calls": [
                        {
                            "id": tc.id,
                            "type": "function",
                            "function": {
                                "name": tc.name,
                                "arguments": json.dumps(tc.arguments),
                            },
                        }
                        for tc in msg.tool_calls
                    ],
                }
                api_messages.append(api_msg)
            else:
                api_messages.append({
                    "role": msg.role,
                    "content": msg.content,
                })

        kwargs: dict[str, Any] = {
            "model": self._model,
            "messages": api_messages,
            "temperature": temperature,
        }
        if tools:
            kwargs["tools"] = tools

        logger.debug(
            "OpenAI request: is_local=%s, model=%s, roles=%s, tool_count=%d",
            self._is_local,
            self._model,
            [m["role"] for m in api_messages],
            len(tools) if tools else 0,
        )

        response = await self._client.chat.completions.create(**kwargs)

        choice = response.choices[0]
        tool_calls = []

        if self._is_local and choice.message.content:
            # For local models, parse tool calls from text response
            tool_calls = self._parse_tool_calls_from_text(choice.message.content)
            if tool_calls:
                logger.debug("Parsed %d tool call(s) from local model text", len(tool_calls))
        elif choice.message.tool_calls:
            for tc in choice.message.tool_calls:
                tool_calls.append(
                    LLMToolCall(
                        id=tc.id,
                        name=tc.function.name,
                        arguments=json.loads(tc.function.arguments),
                    )
                )

        return LLMResponse(
            content=choice.message.content or "",
            tool_calls=tool_calls,
            finish_reason=choice.finish_reason or "stop",
            usage={
                "prompt_tokens": response.usage.prompt_tokens if response.usage else 0,
                "completion_tokens": response.usage.completion_tokens if response.usage else 0,
            },
        )

    # ── Local model helpers ──────────────────────────────────────────

    @staticmethod
    def _fold_system_messages(messages: list[Message]) -> list[Message]:
        """Merge system messages into the first user message.

        Many local model templates (Mistral, Llama, etc.) only support
        ``user`` and ``assistant`` roles.  This folds any leading system
        messages into the first user message as a preamble.
        """
        system_parts: list[str] = []
        rest: list[Message] = []
        for msg in messages:
            if msg.role == "system" and not rest:
                system_parts.append(msg.content)
            else:
                rest.append(msg)

        if not system_parts:
            return messages

        prefix = "\n\n".join(system_parts)
        result: list[Message] = []
        injected = False
        for msg in rest:
            if msg.role == "user" and not injected:
                result.append(Message(
                    role="user",
                    content=f"[System Instructions]\n{prefix}\n\n[User Message]\n{msg.content}",
                ))
                injected = True
            else:
                result.append(msg)

        if not injected:
            result.insert(0, Message(role="user", content=prefix))

        return result

    @staticmethod
    def _build_tool_prompt(tools: list[dict[str, Any]]) -> str:
        """Build a text description of available tools for local models."""
        lines = [
            "You have access to the following tools. To use a tool, respond with ONLY "
            "a JSON object in this exact format (no other text):",
            "",
            '{"name": "tool_name", "arguments": {"param1": "value1"}}',
            "",
            "If you do not need to use a tool, respond with normal text.",
            "",
            "Available tools:",
        ]
        for tool in tools:
            func = tool.get("function", tool)
            name = func.get("name", "unknown")
            desc = func.get("description", "")
            params = func.get("parameters", {})
            lines.append(f"\n## {name}")
            if desc:
                lines.append(desc)
            props = params.get("properties", {})
            if props:
                required = set(params.get("required", []))
                lines.append("Parameters:")
                for pname, pinfo in props.items():
                    ptype = pinfo.get("type", "any")
                    pdesc = pinfo.get("description", "")
                    req_label = "required" if pname in required else "optional"
                    lines.append(f"  - {pname} ({ptype}, {req_label}): {pdesc}")
        return "\n".join(lines)

    @classmethod
    def _embed_tools_in_prompt(
        cls, messages: list[Message], tools: list[dict[str, Any]],
    ) -> list[Message]:
        """Embed tool definitions as text in the first user message."""
        tool_text = cls._build_tool_prompt(tools)
        result: list[Message] = []
        injected = False
        for msg in messages:
            if msg.role == "user" and not injected:
                result.append(Message(
                    role="user",
                    content=f"{msg.content}\n\n{tool_text}",
                ))
                injected = True
            else:
                result.append(msg)
        if not injected:
            result.insert(0, Message(role="user", content=tool_text))
        return result

    @staticmethod
    def _flatten_for_local(messages: list[Message]) -> list[Message]:
        """Convert all messages to user/assistant roles only for local models.

        Also merges consecutive same-role messages to maintain the strict
        alternation required by most local model chat templates.
        """
        flat: list[Message] = []
        for msg in messages:
            if msg.role == "tool":
                # Tool results become user messages
                flat.append(Message(
                    role="user",
                    content=f"[Tool Result]\n{msg.content}",
                ))
            elif msg.role == "assistant":
                # Use content as-is (already contains tool call text for local)
                flat.append(Message(role="assistant", content=msg.content or ""))
            elif msg.role == "user":
                flat.append(msg)
            else:
                # Any other role (shouldn't happen after folding) → user
                flat.append(Message(role="user", content=msg.content))

        # Merge consecutive same-role messages for strict alternation
        if not flat:
            return flat
        merged: list[Message] = [flat[0]]
        for msg in flat[1:]:
            if msg.role == merged[-1].role:
                merged[-1] = Message(
                    role=msg.role,
                    content=f"{merged[-1].content}\n\n{msg.content}",
                )
            else:
                merged.append(msg)
        return merged

    # ── Text-based tool call parsing ─────────────────────────────────

    @staticmethod
    def _parse_tool_calls_from_text(content: str) -> list[LLMToolCall]:
        """Try to parse tool call JSON from the model's text response."""
        tool_calls: list[LLMToolCall] = []

        # Strategy 1: Try the whole content stripped as JSON
        stripped = content.strip()
        # Remove markdown code fences if wrapping the whole response
        if stripped.startswith("```"):
            first_newline = stripped.find("\n")
            if first_newline != -1:
                inner = stripped[first_newline + 1:]
                if inner.rstrip().endswith("```"):
                    stripped = inner.rstrip()[:-3].strip()

        tc = OpenAIProvider._try_parse_tool_call(stripped)
        if tc:
            return [tc]

        # Strategy 2: Look for ```json ... ``` blocks
        for match in re.finditer(r"```(?:json)?\s*\n?(.*?)\n?\s*```", content, re.DOTALL):
            tc = OpenAIProvider._try_parse_tool_call(match.group(1).strip())
            if tc:
                tool_calls.append(tc)
        if tool_calls:
            return tool_calls

        # Strategy 3: Find JSON objects by brace matching
        for obj in OpenAIProvider._extract_json_objects(content):
            tc = OpenAIProvider._try_parse_tool_call_from_dict(obj)
            if tc:
                tool_calls.append(tc)

        return tool_calls

    @staticmethod
    def _try_parse_tool_call(text: str) -> LLMToolCall | None:
        """Try to parse a single tool call from a JSON string."""
        try:
            data = json.loads(text)
            if isinstance(data, dict):
                return OpenAIProvider._try_parse_tool_call_from_dict(data)
        except (json.JSONDecodeError, ValueError):
            pass
        return None

    @staticmethod
    def _try_parse_tool_call_from_dict(data: dict) -> LLMToolCall | None:
        """Try to interpret a dict as a tool call."""
        name = None
        arguments: dict[str, Any] = {}

        if "name" in data and "arguments" in data:
            name = data["name"]
            arguments = data["arguments"] if isinstance(data["arguments"], dict) else {}
        elif "tool_call" in data and isinstance(data["tool_call"], dict):
            tc_data = data["tool_call"]
            name = tc_data.get("name")
            arguments = tc_data.get("arguments", {})
            if not isinstance(arguments, dict):
                arguments = {}

        if name and isinstance(name, str):
            return LLMToolCall(
                id=f"local_{uuid.uuid4().hex[:8]}",
                name=name,
                arguments=arguments,
            )
        return None

    @staticmethod
    def _extract_json_objects(text: str) -> list[dict]:
        """Extract JSON objects from text by brace matching."""
        objects: list[dict] = []
        i = 0
        while i < len(text):
            if text[i] == "{":
                depth = 0
                start = i
                j = i
                while j < len(text):
                    if text[j] == "{":
                        depth += 1
                    elif text[j] == "}":
                        depth -= 1
                        if depth == 0:
                            try:
                                obj = json.loads(text[start:j + 1])
                                if isinstance(obj, dict):
                                    objects.append(obj)
                            except (json.JSONDecodeError, ValueError):
                                pass
                            i = j + 1
                            break
                    j += 1
                else:
                    i += 1
            else:
                i += 1
        return objects

    # ── Tool formatting ──────────────────────────────────────────────

    def format_tools(self, tool_definitions: list[dict[str, Any]]) -> list[dict[str, Any]]:
        """Convert to OpenAI function calling format."""
        tools = []
        for defn in tool_definitions:
            tool = {
                "type": "function",
                "function": {
                    "name": defn["name"].replace(".", "_"),
                    "description": defn["description"],
                    "parameters": defn["parameters"],
                },
            }
            tools.append(tool)
        return tools
