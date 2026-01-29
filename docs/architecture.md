# Architecture

## Overview

Revit Orchestrator connects LLMs to Autodesk Revit through an MCP (Model Context Protocol) server. It enables AI assistants to read and modify Revit models using a tool-based architecture.

```
┌──────────────────┐     MCP      ┌──────────────────┐  Named Pipe  ┌──────────────────┐
│   LLM Provider   │◄────────────►│  Python MCP      │◄────────────►│  C# Revit        │
│  (Claude/OpenAI) │   (stdio)    │  Server           │   (JSON)     │  Add-in          │
└──────────────────┘              └──────────────────┘              └──────────────────┘
                                         │                                   │
                                    ┌────┴────┐                         ┌────┴────┐
                                    │Adapters │                         │Commands │
                                    ├─────────┤                         ├─────────┤
                                    │ revit   │                         │ GetInfo │
                                    │ pyrevit │                         │ Create  │
                                    │ dynamo  │                         │ Wall    │
                                    │workflow │                         └─────────┘
                                    └─────────┘
```

## Components

### MCP Server (Python)

The MCP server is the central hub. It:

- Registers tools from JSON definition files in `tools/`
- Validates tool call arguments against JSON Schemas
- Routes calls to the appropriate adapter (revit, pyrevit, dynamo, workflow)
- Manages the named pipe connection to the Revit add-in
- Provides a provider-agnostic LLM layer for chat-based interaction

### Revit Add-in (C#)

The add-in runs inside Revit and:

- Connects to the MCP server via a named pipe client
- Receives tool calls on a background thread
- Bridges to the Revit main thread via `ExternalEvent`
- Executes commands within Revit transactions
- Provides a WPF dockable chat panel

### Named Pipe IPC

Communication uses Windows Named Pipes with length-prefixed JSON framing:

- **Pipe name**: `\\.\pipe\RevitOrchestrator`
- **Framing**: 4-byte LE uint32 length + UTF-8 JSON
- **Protocol**: Request/response with UUID correlation

See `contracts/pipe-protocol.md` for the full specification.

## Adapters

### Revit Adapter
Sends tool calls over the named pipe to the C# add-in for direct Revit API execution.

### pyRevit Adapter
Invokes pyRevit CLI scripts as subprocesses with arguments passed via environment variables.

### Dynamo Adapter
Runs Dynamo graphs through the Revit add-in's Dynamo integration.

### Workflow Adapter
Composes multiple tool calls into multi-step flows, calling other adapters via the dispatcher.

## ExternalEvent Bridge

Revit's API is single-threaded. The pipe listener runs on a background thread and cannot call the API directly. The bridge works as follows:

1. Background thread receives a tool call → enqueues a `ToolCallContext` (with a `TaskCompletionSource`)
2. Calls `ExternalEvent.Raise()`
3. Revit calls `RevitCommandHandler.Execute()` on the main thread
4. Handler dispatches to the correct `IRevitCommand`, wraps in transaction if needed
5. Completes the `TaskCompletionSource` with the result
6. Background thread awaits the TCS, sends the result back over the pipe

## Tool Registration

Tools are defined as JSON files in `src/mcp-server/orchestrator/tools/`:

```json
{
  "name": "revit.create_wall",
  "adapter": "revit",
  "description": "Creates a wall...",
  "parameters": { "type": "object", "properties": { ... } }
}
```

A `watchdog` file watcher enables hot-reload: drop a new JSON file and the tool appears immediately in the MCP catalog.

## Multi-Version Revit Support

The C# project uses `Directory.Build.props` to parameterize the Revit version:

```bash
dotnet build -p:RevitVersion=2025
dotnet build -p:RevitVersion=2026
```

Conditional compilation (`#if REVIT2025`) handles API differences between versions.
