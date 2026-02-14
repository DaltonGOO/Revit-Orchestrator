"""Length-prefixed JSON framing for named pipe communication."""

from __future__ import annotations

import json
import struct
import uuid
from datetime import datetime, timezone
from typing import Any

MAX_MESSAGE_SIZE = 16 * 1024 * 1024  # 16 MiB
HEADER_SIZE = 4  # 4 bytes, little-endian uint32


def encode_message(data: dict[str, Any]) -> bytes:
    """Encode a dict as a length-prefixed JSON message.

    Returns 4-byte LE uint32 length prefix followed by UTF-8 JSON payload.
    """
    payload = json.dumps(data, separators=(",", ":")).encode("utf-8")
    if len(payload) > MAX_MESSAGE_SIZE:
        raise ValueError(
            f"Message size {len(payload)} exceeds maximum {MAX_MESSAGE_SIZE}"
        )
    header = struct.pack("<I", len(payload))
    return header + payload


def decode_header(header_bytes: bytes) -> int:
    """Decode a 4-byte LE uint32 length prefix.

    Returns the payload length.
    """
    if len(header_bytes) != HEADER_SIZE:
        raise ValueError(f"Header must be {HEADER_SIZE} bytes, got {len(header_bytes)}")
    (length,) = struct.unpack("<I", header_bytes)
    if length > MAX_MESSAGE_SIZE:
        raise ValueError(
            f"Message size {length} exceeds maximum {MAX_MESSAGE_SIZE}"
        )
    return length


def decode_payload(payload_bytes: bytes) -> dict[str, Any]:
    """Decode a UTF-8 JSON payload into a dict."""
    return json.loads(payload_bytes.decode("utf-8"))


def make_message(
    msg_type: str,
    payload: dict[str, Any] | None = None,
    msg_id: str | None = None,
) -> dict[str, Any]:
    """Create a pipe message envelope.

    Args:
        msg_type: One of 'tool_call', 'tool_result', 'ping', 'pong', 'error'.
        payload: Type-specific payload dict.
        msg_id: Optional message ID; auto-generated if not provided.
    """
    return {
        "id": msg_id or str(uuid.uuid4()),
        "type": msg_type,
        "timestamp": datetime.now(timezone.utc).isoformat(),
        "payload": payload or {},
    }


def make_tool_call(
    tool_name: str,
    args: dict[str, Any],
    execution_mode: str = "headless",
) -> dict[str, Any]:
    """Create a tool_call message."""
    return make_message("tool_call", {
        "tool_name": tool_name,
        "args": args,
        "execution_mode": execution_mode,
    })


def make_tool_result(
    call_id: str,
    success: bool,
    data: dict[str, Any],
    duration_ms: int,
    error: dict[str, str] | None = None,
) -> dict[str, Any]:
    """Create a tool_result message."""
    return make_message(
        "tool_result",
        {
            "call_id": call_id,
            "success": success,
            "data": data,
            "error": error,
            "duration_ms": duration_ms,
        },
    )


def make_error(code: str, message: str, call_id: str | None = None) -> dict[str, Any]:
    """Create an error message."""
    payload: dict[str, Any] = {"code": code, "message": message}
    if call_id:
        payload["call_id"] = call_id
    return make_message("error", payload)


def make_ping() -> dict[str, Any]:
    """Create a ping message."""
    return make_message("ping")


def make_pong() -> dict[str, Any]:
    """Create a pong message."""
    return make_message("pong")


def make_chat_message(content: str) -> dict[str, Any]:
    """Create a chat_message (C# → Python)."""
    return make_message("chat_message", {"content": content})


def make_chat_response(
    content: str,
    tool_calls: list[dict[str, Any]] | None = None,
    is_final: bool = True,
) -> dict[str, Any]:
    """Create a chat_response (Python → C#)."""
    return make_message(
        "chat_response",
        {
            "content": content,
            "tool_calls": tool_calls or [],
            "is_final": is_final,
        },
    )


def make_chat_status(status: str) -> dict[str, Any]:
    """Create a chat_status (Python → C#)."""
    return make_message("chat_status", {"status": status})


def make_tool_list_request() -> dict[str, Any]:
    """Create a tool_list_request (C# → Python)."""
    return make_message("tool_list_request")


def make_tool_list_response(tools: list[dict[str, Any]]) -> dict[str, Any]:
    """Create a tool_list_response (Python → C#)."""
    return make_message("tool_list_response", {"tools": tools})


def make_tool_add_request(
    file_path: str,
    metadata: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """Create a tool_add_request (C# → Python)."""
    payload: dict[str, Any] = {"file_path": file_path}
    if metadata:
        payload["metadata"] = metadata
    return make_message("tool_add_request", payload)


def make_tool_add_response(
    success: bool,
    tool_name: str = "",
    error: str = "",
    tool: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """Create a tool_add_response (Python → C#)."""
    return make_message(
        "tool_add_response",
        {
            "success": success,
            "tool_name": tool_name,
            "error": error,
            "tool": tool or {},
        },
    )


def make_tool_delete_request(tool_name: str) -> dict[str, Any]:
    """Create a tool_delete_request (C# → Python)."""
    return make_message("tool_delete_request", {"tool_name": tool_name})


def make_tool_delete_response(
    success: bool,
    tool_name: str = "",
    error: str = "",
) -> dict[str, Any]:
    """Create a tool_delete_response (Python → C#)."""
    return make_message(
        "tool_delete_response",
        {
            "success": success,
            "tool_name": tool_name,
            "error": error,
        },
    )


def make_execution_event(
    event_type: str,
    tool_name: str,
    args: dict[str, Any],
    correlation_id: str,
    sequence: int,
    event_id: str | None = None,
    result: dict[str, Any] | None = None,
    duration_ms: int = 0,
    error: str | None = None,
    origin: str = "chat",
) -> dict[str, Any]:
    """Create an execution_event (Python → C#).

    Args:
        event_type: One of 'started', 'completed', 'failed'.
        tool_name: Name of the tool being executed.
        args: Tool arguments dictionary.
        correlation_id: Groups related calls in one agentic loop.
        sequence: Order within the correlation group.
        event_id: Unique event ID (auto-generated if not provided).
        result: Tool execution result (for completed events).
        duration_ms: Execution duration in milliseconds.
        error: Error message (for failed events).
        origin: Source of the execution: 'chat', 'manual', or 'test'.
    """
    return make_message(
        "execution_event",
        {
            "event_id": event_id or str(uuid.uuid4()),
            "event_type": event_type,
            "tool_name": tool_name,
            "args": args,
            "correlation_id": correlation_id,
            "sequence": sequence,
            "result": result,
            "duration_ms": duration_ms,
            "error": error,
            "origin": origin,
        },
    )


def make_workflow_save_request(
    name: str,
    description: str,
    steps: list[dict[str, Any]],
    original_name: str = "",
) -> dict[str, Any]:
    """Create a workflow_save_request (C# → Python)."""
    payload: dict[str, Any] = {
        "name": name,
        "description": description,
        "steps": steps,
    }
    if original_name:
        payload["original_name"] = original_name
    return make_message("workflow_save_request", payload)


def make_workflow_save_response(
    success: bool,
    workflow_name: str = "",
    error: str = "",
) -> dict[str, Any]:
    """Create a workflow_save_response (Python → C#)."""
    return make_message(
        "workflow_save_response",
        {
            "success": success,
            "workflow_name": workflow_name,
            "error": error,
        },
    )


def make_status_query() -> dict[str, Any]:
    """Create a status_query message (Python → C#)."""
    return make_message("status_query")


def make_status_response(
    revit_version: str = "",
    document_title: str = "",
    document_guid: str = "",
    is_worksharing: bool = False,
    active_view_type: str = "",
    user_name: str = "",
) -> dict[str, Any]:
    """Create a status_response message (C# → Python)."""
    return make_message(
        "status_response",
        {
            "revit_version": revit_version,
            "document_title": document_title,
            "document_guid": document_guid,
            "is_worksharing": is_worksharing,
            "active_view_type": active_view_type,
            "user_name": user_name,
        },
    )


def make_approval_request(
    tool_name: str,
    args: dict[str, Any],
    side_effects: list[str],
    call_id: str | None = None,
) -> dict[str, Any]:
    """Create an approval_request message (Python → C#)."""
    return make_message(
        "approval_request",
        {
            "tool_name": tool_name,
            "args": args,
            "side_effects": side_effects,
            "call_id": call_id or "",
        },
    )


def make_approval_response(approved: bool, call_id: str = "") -> dict[str, Any]:
    """Create an approval_response message (C# → Python)."""
    return make_message(
        "approval_response",
        {
            "approved": approved,
            "call_id": call_id,
        },
    )


def make_workflow_suggestion(suggestion: dict[str, Any]) -> dict[str, Any]:
    """Create a workflow_suggestion message (Python → C#)."""
    return make_message("workflow_suggestion", suggestion)


def make_workflow_load_request(workflow_name: str) -> dict[str, Any]:
    """Create a workflow_load_request (C# → Python)."""
    return make_message("workflow_load_request", {"workflow_name": workflow_name})


def make_workflow_load_response(
    success: bool,
    workflow: dict[str, Any] | None = None,
    error: str = "",
) -> dict[str, Any]:
    """Create a workflow_load_response (Python → C#)."""
    return make_message(
        "workflow_load_response",
        {
            "success": success,
            "workflow": workflow or {},
            "error": error,
        },
    )


def make_workflow_review_request(
    steps: list[dict[str, Any]],
    description: str,
) -> dict[str, Any]:
    """Create a workflow_review_request (C# → Python)."""
    return make_message(
        "workflow_review_request",
        {
            "steps": steps,
            "description": description,
        },
    )


def make_workflow_review_response(
    success: bool,
    summary: str = "",
    flags: list[str] | None = None,
    suggestions: list[dict[str, Any]] | None = None,
    error: str = "",
) -> dict[str, Any]:
    """Create a workflow_review_response (Python → C#)."""
    return make_message(
        "workflow_review_response",
        {
            "success": success,
            "summary": summary,
            "flags": flags or [],
            "suggestions": suggestions or [],
            "error": error,
        },
    )


def make_workflow_test_request(
    steps: list[dict[str, Any]],
    description: str = "",
) -> dict[str, Any]:
    """Create a workflow_test_request (C# → Python)."""
    return make_message(
        "workflow_test_request",
        {
            "steps": steps,
            "description": description,
        },
    )


def make_workflow_test_response(
    success: bool,
    steps_completed: int = 0,
    steps_total: int = 0,
    step_summaries: dict[str, Any] | None = None,
    skipped_steps: list[str] | None = None,
    total_duration_ms: int = 0,
    error: str = "",
) -> dict[str, Any]:
    """Create a workflow_test_response (Python → C#)."""
    return make_message(
        "workflow_test_response",
        {
            "success": success,
            "steps_completed": steps_completed,
            "steps_total": steps_total,
            "step_summaries": step_summaries or {},
            "skipped_steps": skipped_steps or [],
            "total_duration_ms": total_duration_ms,
            "error": error,
        },
    )


def make_workflow_run_request(
    workflow_name: str,
    input_args: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """Create a workflow_run_request (C# → Python)."""
    return make_message(
        "workflow_run_request",
        {
            "workflow_name": workflow_name,
            "input_args": input_args or {},
        },
    )


def make_workflow_run_response(
    success: bool,
    data: dict[str, Any] | None = None,
    error: str = "",
) -> dict[str, Any]:
    """Create a workflow_run_response (Python → C#)."""
    return make_message(
        "workflow_run_response",
        {
            "success": success,
            "data": data or {},
            "error": error,
        },
    )


def make_tool_run_request(
    tool_name: str,
    input_args: dict[str, Any] | None = None,
    execution_mode: str = "headless",
) -> dict[str, Any]:
    """Create a tool_run_request (C# → Python)."""
    return make_message(
        "tool_run_request",
        {
            "tool_name": tool_name,
            "input_args": input_args or {},
            "execution_mode": execution_mode,
        },
    )


def make_tool_run_response(
    success: bool,
    data: dict[str, Any] | None = None,
    error: str = "",
) -> dict[str, Any]:
    """Create a tool_run_response (Python → C#)."""
    return make_message(
        "tool_run_response",
        {
            "success": success,
            "data": data or {},
            "error": error,
        },
    )


def make_tool_load_request(tool_name: str) -> dict[str, Any]:
    """Create a tool_load_request (C# → Python)."""
    return make_message("tool_load_request", {"tool_name": tool_name})


def make_tool_load_response(
    success: bool,
    tool: dict[str, Any] | None = None,
    error: str = "",
) -> dict[str, Any]:
    """Create a tool_load_response (Python → C#)."""
    return make_message(
        "tool_load_response",
        {
            "success": success,
            "tool": tool or {},
            "error": error,
        },
    )


def make_tool_update_request(
    tool_name: str,
    definition: dict[str, Any],
) -> dict[str, Any]:
    """Create a tool_update_request (C# → Python)."""
    return make_message(
        "tool_update_request",
        {
            "tool_name": tool_name,
            "definition": definition,
        },
    )


def make_tool_update_response(
    success: bool,
    tool_name: str = "",
    error: str = "",
) -> dict[str, Any]:
    """Create a tool_update_response (Python → C#)."""
    return make_message(
        "tool_update_response",
        {
            "success": success,
            "tool_name": tool_name,
            "error": error,
        },
    )


# ------------------------------------------------------------------
# Screenshot capture (Python → C# → Python)
# ------------------------------------------------------------------

def make_screenshot_request() -> dict[str, Any]:
    """Create a screenshot_request message (Python → C#)."""
    return make_message("screenshot_request")


def make_screenshot_response(
    call_id: str,
    success: bool,
    image_base64: str | None = None,
    mime_type: str = "image/png",
) -> dict[str, Any]:
    """Create a screenshot_response message (C# → Python)."""
    return make_message(
        "screenshot_response",
        {
            "call_id": call_id,
            "success": success,
            "image_base64": image_base64,
            "mime_type": mime_type,
        },
    )


# ------------------------------------------------------------------
# Context request (extended status query)
# ------------------------------------------------------------------

def make_context_request() -> dict[str, Any]:
    """Create a context_request message (Python → C#).

    Like status_query but intended for episode enrichment.
    """
    return make_message("context_request")


def make_context_response(
    call_id: str,
    revit_version: str = "",
    document_title: str = "",
    document_guid: str = "",
    is_worksharing: bool = False,
    active_view_type: str = "",
    user_name: str = "",
) -> dict[str, Any]:
    """Create a context_response message (C# → Python)."""
    return make_message(
        "context_response",
        {
            "call_id": call_id,
            "revit_version": revit_version,
            "document_title": document_title,
            "document_guid": document_guid,
            "is_worksharing": is_worksharing,
            "active_view_type": active_view_type,
            "user_name": user_name,
        },
    )


# ------------------------------------------------------------------
# Element identity request
# ------------------------------------------------------------------

def make_element_identity_request(
    element_ids: list[int],
) -> dict[str, Any]:
    """Create an element_identity_request message (Python → C#)."""
    return make_message(
        "element_identity_request",
        {"element_ids": element_ids},
    )


def make_element_identity_response(
    call_id: str,
    success: bool,
    identities: list[dict[str, Any]] | None = None,
) -> dict[str, Any]:
    """Create an element_identity_response message (C# → Python)."""
    return make_message(
        "element_identity_response",
        {
            "call_id": call_id,
            "success": success,
            "identities": identities or [],
        },
    )


# ------------------------------------------------------------------
# Settings request (C# → Python → C#)
# ------------------------------------------------------------------

def make_settings_request() -> dict[str, Any]:
    """Create a settings_request message (C# → Python)."""
    return make_message("settings_request")


def make_settings_response(settings: dict[str, Any]) -> dict[str, Any]:
    """Create a settings_response (Python → C#)."""
    return make_message("settings_response", settings)
