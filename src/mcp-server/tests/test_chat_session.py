"""Tests for the agentic-loop bail-out conditions in ChatSession.

The agent used to thrash through unrelated fallbacks when one tool failed
(see commit history). These tests pin down the new caps so the regression
can't sneak back in.
"""

from __future__ import annotations

from typing import Any

import pytest

from orchestrator.chat_session import (
    MAX_CONSECUTIVE_FAILURES,
    MAX_TOOL_CALLS_PER_TURN,
    ChatSession,
)
from orchestrator.dispatcher.result import ToolResult
from orchestrator.llm.base import LLMResponse, LLMToolCall, Message


class _ScriptedRouter:
    """LLM router stand-in that replays a queue of canned responses."""

    def __init__(self, responses: list[LLMResponse]) -> None:
        self._responses = list(responses)
        self.calls: list[list[Message]] = []

    async def chat(self, messages: list[Message]) -> LLMResponse:
        self.calls.append(list(messages))
        if not self._responses:
            # Defensive: if the loop iterates more than scripted, fall through
            # to a plain text response so tests fail with a clear message
            # instead of an IndexError deep in the loop.
            return LLMResponse(content="(no more scripted responses)")
        return self._responses.pop(0)


class _ScriptedDispatcher:
    """Dispatcher stand-in that replays canned tool results in call order."""

    def __init__(self, results: list[ToolResult]) -> None:
        self._results = list(results)
        self.calls: list[tuple[str, dict[str, Any]]] = []

    async def dispatch(self, name: str, args: dict[str, Any]) -> ToolResult:
        self.calls.append((name, args))
        if not self._results:
            return ToolResult.fail("EXHAUSTED", "no more scripted results")
        return self._results.pop(0)


class _RecordingConnection:
    """Pipe connection stand-in that captures every message sent."""

    def __init__(self) -> None:
        self.sent: list[dict[str, Any]] = []

    async def send(self, msg: dict[str, Any]) -> None:
        self.sent.append(msg)

    async def send_and_wait(self, msg: dict[str, Any], timeout: float = 5.0) -> dict[str, Any]:
        # ChatSession uses this only to fetch run context; an empty payload is
        # fine and exercises the non-fatal fallback path.
        return {"payload": {}}


def _tool_response(call_id: str, name: str, args: dict[str, Any] | None = None) -> LLMResponse:
    return LLMResponse(
        content="",
        tool_calls=[LLMToolCall(id=call_id, name=name, arguments=args or {})],
    )


def _final(content: str) -> LLMResponse:
    return LLMResponse(content=content)


def _make_session(
    router: _ScriptedRouter, dispatcher: _ScriptedDispatcher
) -> tuple[ChatSession, _RecordingConnection]:
    conn = _RecordingConnection()
    session = ChatSession(conn, router, dispatcher, audit_log=None)  # type: ignore[arg-type]
    return session, conn


def _final_chat_response(conn: _RecordingConnection) -> dict[str, Any]:
    """Return the last is_final=True chat_response payload sent over the pipe."""
    finals = [
        m for m in conn.sent
        if m.get("type") == "chat_response" and m.get("payload", {}).get("is_final")
    ]
    assert finals, "expected at least one is_final chat_response"
    return finals[-1]["payload"]


@pytest.mark.asyncio
async def test_bails_after_consecutive_failures():
    """Two failed calls in a row trigger an abort with a recap message."""
    router = _ScriptedRouter([
        _tool_response("c1", "dynamo.run_graph"),
        _tool_response("c2", "pyrevit.run_script"),
    ])
    dispatcher = _ScriptedDispatcher([
        ToolResult.fail("DYNAMO_INPUT_TYPE_UNSUPPORTED",
                        "Could not set Categories"),
        ToolResult.fail("FILE_NOT_FOUND", "Script not found"),
    ])
    session, conn = _make_session(router, dispatcher)

    await session.handle_user_message({"payload": {"content": "do the thing"}})

    assert len(dispatcher.calls) == MAX_CONSECUTIVE_FAILURES, (
        "loop should stop the moment the failure threshold is hit, not "
        "continue into another LLM round-trip"
    )
    final = _final_chat_response(conn)
    assert "stopping to check in" in final["content"]
    assert "dynamo.run_graph" in final["content"]
    assert "pyrevit.run_script" in final["content"]
    assert "What would you like me to do?" in final["content"]


@pytest.mark.asyncio
async def test_consecutive_counter_resets_on_success():
    """A success between failures resets the counter — fail/ok/fail keeps going."""
    router = _ScriptedRouter([
        _tool_response("c1", "tool.a"),
        _tool_response("c2", "tool.b"),
        _tool_response("c3", "tool.c"),
        _final("done"),
    ])
    dispatcher = _ScriptedDispatcher([
        ToolResult.fail("ERR", "fail 1"),
        ToolResult.ok({"x": 1}),
        ToolResult.fail("ERR", "fail 2"),
    ])
    session, conn = _make_session(router, dispatcher)

    await session.handle_user_message({"payload": {"content": "hi"}})

    assert len(dispatcher.calls) == 3, (
        "fail/ok/fail should not trip the consecutive-failure cap"
    )
    final = _final_chat_response(conn)
    assert final["content"] == "done"


@pytest.mark.asyncio
async def test_bails_after_total_call_limit():
    """Too many calls in one turn aborts even if they all succeed."""
    router = _ScriptedRouter([
        _tool_response(f"c{i}", f"tool.{i}") for i in range(MAX_TOOL_CALLS_PER_TURN + 2)
    ])
    dispatcher = _ScriptedDispatcher([
        ToolResult.ok({"i": i}) for i in range(MAX_TOOL_CALLS_PER_TURN + 2)
    ])
    session, conn = _make_session(router, dispatcher)

    await session.handle_user_message({"payload": {"content": "thrash"}})

    assert len(dispatcher.calls) == MAX_TOOL_CALLS_PER_TURN, (
        "loop should stop at the per-turn cap regardless of success"
    )
    final = _final_chat_response(conn)
    assert f"{MAX_TOOL_CALLS_PER_TURN}-call limit" in final["content"]


@pytest.mark.asyncio
async def test_normal_loop_unaffected_when_under_caps():
    """A clean tool call followed by a final response still works."""
    router = _ScriptedRouter([
        _tool_response("c1", "tool.x"),
        _final("done"),
    ])
    dispatcher = _ScriptedDispatcher([ToolResult.ok({"ok": True})])
    session, conn = _make_session(router, dispatcher)

    await session.handle_user_message({"payload": {"content": "go"}})

    assert len(dispatcher.calls) == 1
    final = _final_chat_response(conn)
    assert final["content"] == "done"
    # No abort marker in any message
    abort_marker = "stopping to check in"
    assert not any(
        abort_marker in m.get("payload", {}).get("content", "")
        for m in conn.sent
    )
