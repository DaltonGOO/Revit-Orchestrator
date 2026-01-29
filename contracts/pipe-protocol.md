# Named Pipe Protocol

## Overview

Communication between the Python MCP server and the C# Revit add-in uses a Windows Named Pipe with length-prefixed JSON framing.

## Pipe Name

```
\\.\pipe\RevitOrchestrator
```

## Framing

Each message is transmitted as:

```
[4 bytes: uint32 LE length] [N bytes: UTF-8 JSON payload]
```

- Length prefix is a **little-endian unsigned 32-bit integer** representing the byte length of the JSON payload.
- Maximum message size: **16 MiB** (16,777,216 bytes).
- Messages exceeding this limit must be rejected with `PIPE_MESSAGE_TOO_LARGE`.

## Message Envelope

All messages share this envelope structure:

```json
{
  "id": "uuid-v4",
  "type": "tool_call | tool_result | ping | pong | error",
  "timestamp": "2025-01-15T10:30:00.000Z",
  "payload": { }
}
```

| Field       | Type   | Description                                    |
|-------------|--------|------------------------------------------------|
| `id`        | string | UUIDv4 unique to this message                  |
| `type`      | string | One of: `tool_call`, `tool_result`, `ping`, `pong`, `error` |
| `timestamp` | string | ISO-8601 timestamp with milliseconds           |
| `payload`   | object | Type-specific payload                          |

## Message Types

### `tool_call` (Python → C#)

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "type": "tool_call",
  "timestamp": "2025-01-15T10:30:00.000Z",
  "payload": {
    "tool_name": "revit.create_wall",
    "args": {
      "start_point": [0, 0, 0],
      "end_point": [10, 0, 0],
      "height": 3.0,
      "wall_type": "Generic - 200mm"
    }
  }
}
```

### `tool_result` (C# → Python)

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440001",
  "type": "tool_result",
  "timestamp": "2025-01-15T10:30:00.045Z",
  "payload": {
    "call_id": "550e8400-e29b-41d4-a716-446655440000",
    "success": true,
    "data": {
      "element_id": 12345,
      "message": "Wall created successfully"
    },
    "error": null,
    "duration_ms": 45
  }
}
```

### `ping` / `pong`

Used for keep-alive and connection health checks.

```json
{ "id": "...", "type": "ping", "timestamp": "...", "payload": {} }
{ "id": "...", "type": "pong", "timestamp": "...", "payload": {} }
```

### `error`

```json
{
  "id": "...",
  "type": "error",
  "timestamp": "...",
  "payload": {
    "code": "TOOL_NOT_FOUND",
    "message": "No handler registered for tool 'revit.unknown_tool'",
    "call_id": "550e8400-e29b-41d4-a716-446655440000"
  }
}
```

## Connection Lifecycle

1. Python server creates the named pipe and listens.
2. C# add-in connects as a client when Revit starts.
3. Both sides exchange `ping`/`pong` every 30 seconds.
4. If no `pong` is received within 10 seconds, the connection is considered dead.
5. C# add-in reconnects automatically with exponential backoff (1s, 2s, 4s, max 30s).
