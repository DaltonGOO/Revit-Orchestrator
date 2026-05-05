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


def make_chat_cancel() -> dict[str, Any]:
    """Create a chat_cancel (C# → Python). Asks Python to abort the in-flight
    chat task so the UI doesn't sit forever on a stuck tool call."""
    return make_message("chat_cancel")


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
    error_code: str = "",
    stage: str = "",
) -> dict[str, Any]:
    """Create a tool_run_response (Python → C#).

    Args:
        success: Whether the tool execution succeeded.
        data: Result data from the tool.
        error: Human-readable error message (empty on success).
        error_code: Machine-readable error code (e.g. SCHEMA_VALIDATION_FAILED).
        stage: Execution stage where failure occurred
               (preflight_validation, dispatch, adapter_execution, postprocess).
    """
    payload: dict[str, Any] = {
        "success": success,
        "data": data or {},
        "error": error,
    }
    if error_code:
        payload["error_code"] = error_code
    if stage:
        payload["stage"] = stage
    return make_message("tool_run_response", payload)


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


# ------------------------------------------------------------------
# Settings update (C# → Python → C#)
# ------------------------------------------------------------------

def make_settings_update_request(settings: dict[str, Any]) -> dict[str, Any]:
    """Create a settings_update_request message (C# → Python).

    Carries {llm_provider, api_key, model, base_url}.
    """
    return make_message("settings_update_request", settings)


def make_settings_update_response(
    success: bool,
    error: str = "",
) -> dict[str, Any]:
    """Create a settings_update_response message (Python → C#)."""
    return make_message(
        "settings_update_response",
        {
            "success": success,
            "error": error,
        },
    )


# ------------------------------------------------------------------
# MCP Connection management (C# ↔ Python)
# ------------------------------------------------------------------

def make_connection_list_request() -> dict[str, Any]:
    """Create a connection_list_request message (C# → Python)."""
    return make_message("connection_list_request")


def make_connection_list_response(
    connections: list[dict[str, Any]],
) -> dict[str, Any]:
    """Create a connection_list_response (Python → C#)."""
    return make_message(
        "connection_list_response",
        {"connections": connections},
    )


def make_connection_add_request(
    connection: dict[str, Any],
) -> dict[str, Any]:
    """Create a connection_add_request (C# → Python)."""
    return make_message("connection_add_request", connection)


def make_connection_add_response(
    success: bool,
    connection: dict[str, Any] | None = None,
    error: str = "",
) -> dict[str, Any]:
    """Create a connection_add_response (Python → C#)."""
    return make_message(
        "connection_add_response",
        {
            "success": success,
            "connection": connection or {},
            "error": error,
        },
    )


def make_connection_update_request(
    connection_id: str,
    updates: dict[str, Any],
) -> dict[str, Any]:
    """Create a connection_update_request (C# → Python)."""
    payload = dict(updates)
    payload["connection_id"] = connection_id
    return make_message("connection_update_request", payload)


def make_connection_update_response(
    success: bool,
    connection: dict[str, Any] | None = None,
    error: str = "",
) -> dict[str, Any]:
    """Create a connection_update_response (Python → C#)."""
    return make_message(
        "connection_update_response",
        {
            "success": success,
            "connection": connection or {},
            "error": error,
        },
    )


def make_connection_delete_request(connection_id: str) -> dict[str, Any]:
    """Create a connection_delete_request (C# → Python)."""
    return make_message(
        "connection_delete_request",
        {"connection_id": connection_id},
    )


def make_connection_delete_response(
    success: bool,
    error: str = "",
) -> dict[str, Any]:
    """Create a connection_delete_response (Python → C#)."""
    return make_message(
        "connection_delete_response",
        {
            "success": success,
            "error": error,
        },
    )


def make_connection_test_request(connection_id: str) -> dict[str, Any]:
    """Create a connection_test_request (C# → Python)."""
    return make_message(
        "connection_test_request",
        {"connection_id": connection_id},
    )


def make_connection_test_response(
    success: bool,
    tools: list[dict[str, Any]] | None = None,
    server_name: str = "",
    server_version: str = "",
    error: str = "",
) -> dict[str, Any]:
    """Create a connection_test_response (Python → C#)."""
    return make_message(
        "connection_test_response",
        {
            "success": success,
            "discovered_tools": tools or [],
            "server_name": server_name,
            "server_version": server_version,
            "error": error,
        },
    )


def make_connection_toggle_request(
    connection_id: str,
    enabled: bool,
) -> dict[str, Any]:
    """Create a connection_toggle_request (C# → Python)."""
    return make_message(
        "connection_toggle_request",
        {
            "connection_id": connection_id,
            "enabled": enabled,
        },
    )


def make_connection_toggle_response(
    success: bool,
    connection: dict[str, Any] | None = None,
    error: str = "",
) -> dict[str, Any]:
    """Create a connection_toggle_response (Python → C#)."""
    return make_message(
        "connection_toggle_response",
        {
            "success": success,
            "connection": connection or {},
            "error": error,
        },
    )


def make_connection_status_event(
    connection_id: str,
    status: str,
    error: str | None = None,
) -> dict[str, Any]:
    """Create a connection_status_event (Python → C# push)."""
    return make_message(
        "connection_status_event",
        {
            "connection_id": connection_id,
            "status": status,
            "error": error,
        },
    )


# ------------------------------------------------------------------
# Reconciliation mapping (Python → C# → Python)
# ------------------------------------------------------------------

def make_mapping_request(
    mapping_type: str,
    recorded_value: str,
    available_options: list[dict[str, Any]],
    best_match: dict[str, Any] | None = None,
    call_id: str | None = None,
) -> dict[str, Any]:
    """Create a mapping_request message (Python → C#).

    Asks the user to reconcile a type mismatch during workflow replay.

    Args:
        mapping_type: One of 'system_type', 'family_type', 'host_element'.
        recorded_value: The name/identifier from the recorded workflow.
        available_options: List of available options in the target model.
        best_match: Pre-selected best match, if one was found.
        call_id: Optional call ID for correlation.
    """
    return make_message(
        "mapping_request",
        {
            "mapping_type": mapping_type,
            "recorded_value": recorded_value,
            "available_options": available_options,
            "best_match": best_match,
            "call_id": call_id or "",
        },
    )


def make_mapping_response(
    action: str,
    selected_option: dict[str, Any] | None = None,
    call_id: str = "",
) -> dict[str, Any]:
    """Create a mapping_response message (C# → Python).

    Args:
        action: One of 'map' (use selected), 'create' (create new), 'cancel'.
        selected_option: The option the user selected (for 'map' action).
        call_id: Correlation ID from the original request.
    """
    return make_message(
        "mapping_response",
        {
            "action": action,
            "selected_option": selected_option,
            "call_id": call_id,
        },
    )


# ------------------------------------------------------------------
# Dynamo run-graph dialog (Python → C# → Python)
#
# Powers `dynamo.run_graph_interactive`: Python pushes an "open dialog"
# request when an LLM tool call wants the user to fill the form, and waits
# for the result that the C# add-in returns once the user clicks Run/Cancel.
# ------------------------------------------------------------------

def make_dynamo_open_run_dialog_request(
    graph_path: str,
    suggested_inputs: dict[str, Any] | None = None,
    call_id: str | None = None,
) -> dict[str, Any]:
    """Create a dynamo_open_run_dialog_request (Python → C#)."""
    return make_message(
        "dynamo_open_run_dialog_request",
        {
            "graph_path": graph_path,
            "suggested_inputs": suggested_inputs or {},
            "call_id": call_id or "",
        },
    )


def make_dynamo_run_dialog_result(
    status: str,
    inputs_used: dict[str, Any] | None = None,
    result: dict[str, Any] | None = None,
    error: str | None = None,
    call_id: str = "",
) -> dict[str, Any]:
    """Create a dynamo_run_dialog_result message (C# → Python).

    Args:
        status: One of 'ran' (user clicked Run, graph executed),
                'cancelled' (user closed/cancelled the dialog),
                'error' (something failed in the dialog).
        inputs_used: The values the user entered (when status='ran').
        result: The result dict from dynamo.run_graph (when status='ran').
        error: Human-readable error string (when status='error').
        call_id: Correlation ID from the original open request.
    """
    return make_message(
        "dynamo_run_dialog_result",
        {
            "status": status,
            "inputs_used": inputs_used or {},
            "result": result or {},
            "error": error or "",
            "call_id": call_id,
        },
    )
