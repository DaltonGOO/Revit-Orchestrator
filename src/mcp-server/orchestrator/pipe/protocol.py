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


def make_tool_call(tool_name: str, args: dict[str, Any]) -> dict[str, Any]:
    """Create a tool_call message."""
    return make_message("tool_call", {"tool_name": tool_name, "args": args})


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
