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
    make_screenshot_request,
)

logger = logging.getLogger(__name__)

MAX_TOOL_ITERATIONS = 10


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
        self._audit_log = audit_log

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
        except Exception:
            logger.exception("Error in chat session agentic loop")
            try:
                await self._send_response("An error occurred while processing your message.", is_final=True)
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
                    # Capture screenshot after successful tool execution
                    await self._capture_screenshot(event_id)
                else:
                    await self._logger.log_failed(
                        event_id,
                        result.error_message or "Unknown error",
                        tc.name,
                        tc.arguments,
                    )

                result_text = json.dumps(result.to_dict())

                self._history.append(
                    Message(
                        role="tool",
                        content=result_text,
                        tool_call_id=tc.id,
                    )
                )
                tool_summaries.append({"name": tc.name, "success": result.success})

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

    async def _capture_screenshot(self, event_id: str) -> None:
        """Request a viewport screenshot from C# and store as an artifact."""
        if self._audit_log is None:
            logger.debug("Screenshot skipped: no audit log")
            return
        if not hasattr(self._audit_log, "blob_store"):
            logger.debug("Screenshot skipped: audit log has no blob_store attribute")
            return
        blob_store = self._audit_log.blob_store
        if blob_store is None:
            logger.debug("Screenshot skipped: blob_store is None")
            return

        try:
            msg = make_screenshot_request()
            response = await self._connection.send_and_wait(msg, timeout=10.0)
            payload = response.get("payload", {})
            if not payload.get("success"):
                logger.debug("Screenshot skipped: C# returned success=false")
                return

            image_b64 = payload.get("image_base64")
            if not image_b64:
                logger.debug("Screenshot skipped: no image_base64 in response")
                return

            import base64
            image_bytes = base64.b64decode(image_b64)
            sha, rel_path = blob_store.put(image_bytes, ext=".png", compress=False)

            self._audit_log.add_artifact(
                event_id=event_id,
                kind="screenshot",
                sha256=sha,
                path=rel_path,
                size_bytes=len(image_bytes),
                mime_type=payload.get("mime_type", "image/png"),
            )
            logger.debug("Stored screenshot artifact for event %s (%d bytes)", event_id, len(image_bytes))
        except Exception:
            logger.exception("Screenshot capture failed (non-fatal)")

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
