# Adding Tools

This guide explains how to add new tools to Revit Orchestrator.

## Overview

A tool consists of two parts:

1. **Tool Definition** — a JSON file in `src/mcp-server/orchestrator/tools/` that describes the tool's name, parameters, and which adapter handles it
2. **Handler** — a Python module in `src/mcp-server/orchestrator/handlers/` (and optionally a C# command class for `revit` adapter tools)

## Step 1: Create the Tool Definition

Create a JSON file in `src/mcp-server/orchestrator/tools/`. The filename should match the tool name: `{adapter}.{tool_name}.json`.

Example: `revit.delete_element.json`

```json
{
  "name": "revit.delete_element",
  "adapter": "revit",
  "description": "Deletes a Revit element by its element ID.",
  "parameters": {
    "type": "object",
    "properties": {
      "element_id": {
        "type": "integer",
        "description": "The element ID to delete"
      }
    },
    "required": ["element_id"]
  },
  "returns": {
    "type": "object",
    "properties": {
      "success": { "type": "boolean" },
      "message": { "type": "string" }
    }
  },
  "examples": [
    {
      "description": "Delete wall with ID 54321",
      "args": { "element_id": 54321 }
    }
  ]
}
```

The definition must conform to `contracts/tool-definition.schema.json`.

## Step 2: Create the Python Handler

Create a Python module at `src/mcp-server/orchestrator/handlers/{name}.py` where dots in the tool name are replaced with underscores.

For `revit.delete_element` → `handlers/revit_delete_element.py`

```python
from __future__ import annotations
from typing import Any
from ..dispatcher.result import ToolResult

async def execute(args: dict[str, Any], **kwargs: Any) -> ToolResult:
    """Handler for revit.delete_element."""
    return ToolResult.ok({
        "message": "Delegated to Revit add-in",
    })
```

For `revit` adapter tools, the handler is a pass-through — the actual execution happens in C#. For `pyrevit` and `dynamo` adapters, the handler contains the execution logic.

## Step 3: Create the C# Command (revit adapter only)

For tools using the `revit` adapter, create a C# command class:

```csharp
using System.Text.Json;
using Autodesk.Revit.DB;
using RevitOrchestrator.Execution;
using RevitOrchestrator.Models;

public sealed class DeleteElementCommand : IRevitCommand
{
    public string ToolName => "revit.delete_element";
    public bool RequiresTransaction => true;

    public ToolResult Execute(Document doc, JsonElement args)
    {
        var elementId = args.GetProperty("element_id").GetInt64();
        var element = doc.GetElement(new ElementId((long)elementId));

        if (element is null)
            return ToolResult.Fail("", "REVIT_API_ERROR", "Element not found");

        doc.Delete(element.Id);

        return ToolResult.Ok("", new Dictionary<string, object?>
        {
            ["success"] = true,
            ["message"] = $"Deleted element {elementId}",
        });
    }
}
```

Register it in `App.cs`:

```csharp
private void RegisterCommands()
{
    CommandDispatcher.Register(new GetElementInfoCommand());
    CommandDispatcher.Register(new CreateWallCommand());
    CommandDispatcher.Register(new DeleteElementCommand()); // Add this
}
```

## Step 4: Verify

If hot-reload is enabled (default), the new tool appears immediately after saving the JSON file. Otherwise, restart the MCP server.

Verify with the MCP inspector:

```bash
mcp dev orchestrator/server.py
```

The new tool should appear in the tool list with its schema.

## Adapter Types

| Adapter    | Handler Behavior | C# Needed? |
|-----------|-----------------|------------|
| `revit`   | Pass-through; real work in C# | Yes |
| `pyrevit` | Runs pyRevit CLI scripts | No |
| `dynamo`  | Runs Dynamo graphs via add-in | No |
| `workflow` | Orchestrates other tool calls | No |

## Workflow Tools

Workflow handlers receive the dispatcher and can call other tools:

```python
async def execute(args: dict[str, Any], dispatcher=None, **kwargs):
    # Call another tool
    result = await dispatcher.dispatch("revit.create_wall", wall_args)
```
