"""Per-connection chat session with agentic tool-call loop."""

from __future__ import annotations

import json
import logging
from typing import Any

from .dispatcher.dispatcher import Dispatcher
from .execution_logger import ExecutionLogger
from .llm.base import LLMResponse, LLMToolCall, Message
from .llm.router import LLMRouter
from .pipe.connection import PipeConnection
from .pipe.protocol import (
    make_chat_response,
    make_chat_status,
    make_context_request,
)

logger = logging.getLogger(__name__)

MAX_TOOL_ITERATIONS = 10

# Bail-out thresholds. The agent typically thrashes through unrelated fallback
# tools when something fails — these caps make it stop and ask the user
# instead, surfacing the real error and saving tokens.
MAX_CONSECUTIVE_FAILURES = 2
MAX_TOOL_CALLS_PER_TURN = 5

# Trim error strings shown back to the user in the abort message — the full
# error is already in the audit log if they need it.
_ERROR_PREVIEW_LEN = 200


class ChatSession:
    """Manages a single conversation over a pipe connection.

    Implements the agentic loop:
    1. User message → LLM
    2. If LLM returns tool_calls → dispatch each → feed results back → repeat
    3. If LLM returns text → send final chat_response to C#
    """

    def __init__(
        self,
        connection: PipeConnection,
        router: LLMRouter,
        dispatcher: Dispatcher,
        audit_log: Any | None = None,
    ) -> None:
        self._connection = connection
        self._router = router
        self._dispatcher = dispatcher
        self._history: list[Message] = []
        self._logger = ExecutionLogger(connection, audit_log=audit_log)

    async def handle_user_message(self, message: dict[str, Any]) -> None:
        """Handle an incoming chat_message from the C# client."""
        content = message.get("payload", {}).get("content", "")
        logger.debug("handle_user_message called, content=%r", content[:100] if content else "")
        if not content:
            logger.warning("Ignoring chat message with empty content. Raw message keys: %s", list(message.keys()))
            return

        self._history.append(Message(role="user", content=content))

        try:
            logger.debug("Sending 'Thinking...' status")
            await self._send_status("Thinking...")
            logger.debug("Starting agentic loop")
            await self._agentic_loop()
            logger.debug("Agentic loop completed successfully")
        except Exception as exc:
            logger.exception("Error in chat session agentic loop")
            # Give the user a specific error message instead of a generic one
            error_msg = "An error occurred while processing your message."
            exc_name = type(exc).__name__
            exc_str = str(exc)
            if "AuthenticationError" in exc_name or "401" in exc_str:
                error_msg = (
                    "**Authentication failed.** Your API key is invalid. "
                    "Please check your API key in Settings.\n\n"
                    "Note: Anthropic keys start with `sk-ant-`, "
                    "OpenAI keys start with `sk-proj-` or `sk-`."
                )
            elif "RateLimitError" in exc_name or "429" in exc_str:
                error_msg = "**Rate limit reached.** Please wait a moment and try again."
            elif "api_key" in exc_str.lower() or "unauthorized" in exc_str.lower():
                error_msg = f"**API error:** {exc_str}"
            else:
                error_msg = f"**Error:** {exc_name}: {exc_str}"
            try:
                await self._send_response(error_msg, is_final=True)
            except Exception:
                logger.exception("Failed to send error response back to client")

    async def _fetch_run_context(self) -> dict[str, Any]:
        """Request current Revit state from the C# add-in via the pipe."""
        try:
            msg = make_context_request()
            response = await self._connection.send_and_wait(msg, timeout=5.0)
            payload = response.get("payload", {})
            return {
                "doc_guid": payload.get("document_guid", ""),
                "doc_title": payload.get("document_title", ""),
                "user_name": payload.get("user_name", ""),
                "revit_version": payload.get("revit_version", ""),
                "active_view_type": payload.get("active_view_type", ""),
                "is_worksharing": payload.get("is_worksharing", False),
            }
        except Exception:
            logger.debug("Failed to fetch run context (non-fatal)")
            return {}

    async def _agentic_loop(self) -> None:
        """Run the LLM → tool-call → LLM loop until a text response or limit."""
        # Fetch run context from Revit before starting
        context = await self._fetch_run_context()
        self._logger.set_run_context(context)

        # Start a new correlation group for this agentic loop
        correlation_id = self._logger.start_correlation()
        logger.debug("Started agentic loop with correlation_id=%s", correlation_id)

        consecutive_failures = 0
        all_calls: list[dict[str, Any]] = []

        for iteration in range(MAX_TOOL_ITERATIONS):
            logger.debug("Agentic loop iteration %d, calling LLM with %d messages", iteration, len(self._history))
            response = await self._router.chat(self._history)
            logger.debug("LLM response: has_tool_calls=%s, content_length=%d",
                         response.has_tool_calls, len(response.content or ""))

            if not response.has_tool_calls:
                # Final text response
                self._history.append(Message(role="assistant", content=response.content))
                await self._send_response(response.content, is_final=True)
                # Log correlation summary with token usage
                usage = None
                if response.usage:
                    usage = {
                        "input_tokens": response.usage.get("input_tokens", 0),
                        "output_tokens": response.usage.get("output_tokens", 0),
                    }
                self._logger.log_correlation_summary(correlation_id, total_usage=usage)
                return

            # Record the assistant message with tool calls
            self._history.append(
                Message(
                    role="assistant",
                    content=response.content,
                    tool_calls=response.tool_calls,
                )
            )

            # Dispatch each tool call
            tool_summaries = []
            for tc in response.tool_calls:
                await self._send_status(f"Calling tool {tc.name}...")

                # Log execution started
                event_id = await self._logger.log_started(tc.name, tc.arguments)

                result = await self._dispatcher.dispatch(tc.name, tc.arguments)

                # Log execution completed or failed
                if result.success:
                    await self._logger.log_completed(event_id, result, tc.name, tc.arguments)
                    consecutive_failures = 0
                else:
                    await self._logger.log_failed(
                        event_id,
                        result.error_message or "Unknown error",
                        tc.name,
                        tc.arguments,
                    )
                    consecutive_failures += 1

                result_text = json.dumps(result.to_dict())

                self._history.append(
                    Message(
                        role="tool",
                        content=result_text,
                        tool_call_id=tc.id,
                    )
                )

                call_summary = {
                    "name": tc.name,
                    "success": result.success,
                    "error": (
                        result.error_message
                        if not result.success and result.error_message
                        else None
                    ),
                }
                tool_summaries.append({"name": tc.name, "success": result.success})
                all_calls.append(call_summary)

                # Bail-out checks: stop the loop when the agent is clearly
                # thrashing instead of letting it burn another LLM round-trip.
                if consecutive_failures >= MAX_CONSECUTIVE_FAILURES:
                    await self._abort_loop(
                        reason=(
                            f"{consecutive_failures} tool calls failed in a row"
                        ),
                        all_calls=all_calls,
                        correlation_id=correlation_id,
                    )
                    return
                if len(all_calls) >= MAX_TOOL_CALLS_PER_TURN:
                    await self._abort_loop(
                        reason=(
                            f"reached the {MAX_TOOL_CALLS_PER_TURN}-call limit "
                            "for one turn"
                        ),
                        all_calls=all_calls,
                        correlation_id=correlation_id,
                    )
                    return

            # Send interim response showing tool calls executed
            await self._send_response(
                response.content,
                tool_calls=tool_summaries,
                is_final=False,
            )
            await self._send_status("Thinking...")

        # Safety valve: hit max iterations
        self._history.append(
            Message(
                role="assistant",
                content="I've reached the maximum number of tool call iterations. Here's what I have so far.",
            )
        )
        await self._send_response(
            "I've reached the maximum number of tool call iterations. "
            "Please try breaking your request into smaller steps.",
            is_final=True,
        )
        self._logger.log_correlation_summary(correlation_id)

    async def _abort_loop(
        self,
        *,
        reason: str,
        all_calls: list[dict[str, Any]],
        correlation_id: str,
    ) -> None:
        """Stop the agentic loop and tell the user what was tried.

        Used when the agent has hit a thrashing condition (consecutive
        failures or too many tool calls in one turn). Surfaces a numbered
        recap so the user can decide what to do, instead of letting the
        agent keep burning round-trips on unrelated fallbacks.
        """
        lines = [
            f"I'm stopping to check in — {reason}.",
            "",
            "Here's what I tried:",
        ]
        for i, c in enumerate(all_calls, 1):
            icon = "✓" if c["success"] else "✗"
            line = f"{i}. {icon} `{c['name']}`"
            if not c["success"] and c.get("error"):
                err = c["error"]
                if len(err) > _ERROR_PREVIEW_LEN:
                    err = err[: _ERROR_PREVIEW_LEN - 1] + "…"
                line += f" — {err}"
            lines.append(line)
        lines.append("")
        lines.append("What would you like me to do?")
        msg = "\n".join(lines)

        self._history.append(Message(role="assistant", content=msg))
        await self._send_response(msg, is_final=True)
        self._logger.log_correlation_summary(correlation_id)

    async def _send_status(self, status: str) -> None:
        """Send a chat_status message to the client."""
        try:
            await self._connection.send(make_chat_status(status))
            logger.debug("Sent chat_status: %s", status)
        except ConnectionError:
            logger.warning("Failed to send status: connection lost")
        except Exception:
            logger.exception("Unexpected error sending status")

    async def _send_response(
        self,
        content: str,
        tool_calls: list[dict[str, Any]] | None = None,
        is_final: bool = True,
    ) -> None:
        """Send a chat_response message to the client."""
        try:
            await self._connection.send(
                make_chat_response(content, tool_calls=tool_calls, is_final=is_final)
            )
            logger.debug("Sent chat_response: is_final=%s, content_length=%d", is_final, len(content or ""))
        except ConnectionError:
            logger.warning("Failed to send response: connection lost")
        except Exception:
            logger.exception("Unexpected error sending response")
