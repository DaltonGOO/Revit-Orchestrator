# Tool Developer Guide

This guide is for Revit automation developers who want to expose their pyRevit scripts, Dynamo graphs, or C# add-in commands as tools in Revit Orchestrator. Registering a tool makes it callable by the AI chat assistant, executable from the UI, and composable into multi-step workflows.

---

## How It Works

Revit Orchestrator runs a Python MCP server that maintains a **tool registry** — an in-memory catalog of JSON tool definitions loaded from `src/mcp-server/orchestrator/tools/`. When a tool is called (by the LLM, a user click, or a workflow step), the **dispatcher** validates the arguments, selects the appropriate **adapter**, and routes the call.

All three adapter types — `revit`, `pyrevit`, and `dynamo` — ultimately send a JSON message over a named pipe to the C# Revit add-in, which executes the actual operation inside Revit's process.

```
 Tool Call (LLM / UI / Workflow)
        |
   Dispatcher
   - validates arguments against JSON Schema
   - checks preconditions
   - selects adapter by "adapter" field
        |
   +----+----+----------+
   |         |           |
 revit    pyrevit     dynamo
   |         |           |
   +----+----+----------+
        |
   Named Pipe (JSON)
        |
   C# Revit Add-in
   - executes Revit API / IronPython / Dynamo
   - returns result + model changes
```

---

## Quick Start

To register a tool, drop a `.json` file into `src/mcp-server/orchestrator/tools/`. If hot-reload is enabled (the default), the tool is available immediately — no restart required.

The filename must match the tool name: a tool named `dynamo.place_furniture` lives in `dynamo.place_furniture.json`.

### Minimal Example

```json
{
  "name": "pyrevit.export_rooms",
  "adapter": "pyrevit",
  "description": "Exports all room data from the active document to a JSON file.",
  "parameters": {
    "type": "object",
    "properties": {
      "script_path": {
        "type": "string",
        "description": "Absolute path to the Python script"
      }
    },
    "required": ["script_path"]
  }
}
```

That's four required fields: `name`, `adapter`, `description`, and `parameters`. Everything else is optional but recommended.

---

## Tool Definition Reference

### Required Fields

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | Unique identifier. Lowercase, dot-separated, minimum two segments. Pattern: `^[a-z][a-z0-9]*(\.[a-z_][a-z0-9_]*)+$` |
| `adapter` | string | One of: `revit`, `pyrevit`, `dynamo`, `workflow`, `mcp` |
| `description` | string | What the tool does (min 10 chars). The LLM reads this to decide when to call your tool, so be specific. |
| `parameters` | object | JSON Schema defining the input arguments (see [Parameters](#parameters)). |

### Optional Fields

| Field | Type | Description |
|-------|------|-------------|
| `returns` | object | JSON Schema describing the output shape. Helps LLM interpret results. |
| `version` | string | Semantic version (`1.0.0`). |
| `author` | string | Who wrote this tool. |
| `tags` | string[] | Categorization tags (e.g., `["mep", "ducts"]`). |
| `examples` | array | Usage examples with `description`, `args`, and optional `expected_output`. |
| `permissions` | object | Access control — see [Permissions](#permissions). |
| `side_effects` | string[] | What the tool changes — see [Side Effects](#side-effects). |
| `preconditions` | array | Checks that must pass before execution — see [Preconditions](#preconditions). |
| `execution` | object | Supported execution modes — see [Execution Modes](#execution-modes). |
| `cost` | object | Performance hints: `estimated_duration_ms`, `cacheable`, `incremental`. |
| `visibility` | string | `"user"` (default) = visible in UI and to LLM. `"llm-only"` = LLM only. `"internal"` = hidden from both. |
| `deprecated` | boolean | Mark a tool as deprecated. |
| `superseded_by` | string | Name of the replacement tool. |

### Parameters

The `parameters` field is a standard JSON Schema object. The dispatcher validates every tool call against this schema before execution.

```json
"parameters": {
  "type": "object",
  "properties": {
    "element_id": {
      "type": "integer",
      "description": "The Revit element ID to look up"
    },
    "include_parameters": {
      "type": "boolean",
      "description": "Whether to include all element parameters",
      "default": true
    }
  },
  "required": ["element_id"]
}
```

Supported JSON Schema types: `string`, `number`, `integer`, `boolean`, `array`, `object`. You can use `enum`, `minimum`, `maximum`, `minItems`, `maxItems`, `default`, `additionalProperties`, and other standard JSON Schema keywords.

Write clear `description` strings for each parameter — the LLM uses these to fill in arguments.

### Permissions

```json
"permissions": {
  "mode": "read",
  "categories": ["Walls", "Floors"],
  "approval_required": false
}
```

| Field | Values | Description |
|-------|--------|-------------|
| `mode` | `"read"`, `"write"` | Whether the tool modifies the Revit model. |
| `categories` | string[] | Revit categories affected (e.g., `"Walls"`, `"Ducts"`, `"Pipes"`). |
| `approval_required` | boolean | If `true`, the user must confirm before execution. |

### Side Effects

Declare what your tool changes so workflows and the UI can warn users appropriately:

```json
"side_effects": ["creates_elements", "modifies_parameters"]
```

Valid values: `creates_elements`, `modifies_elements`, `deletes_elements`, `modifies_parameters`, `changes_view`, `file_io`.

### Preconditions

Checks that must pass before the dispatcher will call your tool:

```json
"preconditions": [
  { "check": "document_open" },
  { "check": "revit_version_gte", "value": "2023" }
]
```

Valid checks: `document_open`, `revit_version_gte`, `worksharing_enabled`.

### Execution Modes

```json
"execution": {
  "modes": ["headless", "interactive"],
  "interactive_hint": "Opens the wall placement dialog for manual endpoint selection."
}
```

- **headless** (default): The tool runs silently using the provided arguments.
- **interactive**: The tool opens its native Revit UI. Use `interactive_hint` to describe what the user sees.

### Examples

Give the LLM concrete usage examples. These also serve as documentation for human users:

```json
"examples": [
  {
    "description": "Create a duct between two points",
    "args": {
      "start_point": [0, 0, 10],
      "end_point": [20, 0, 10],
      "type_name": "Default",
      "level_name": "Level 1"
    }
  }
]
```

---

## Naming Conventions

Tool names use a dot-separated format: `{adapter}.{tool_name}` or `{category}.{subcategory}`.

| Prefix | Adapter | Example |
|--------|---------|---------|
| `revit.*` | `revit` | `revit.create_wall`, `revit.get_element_info` |
| `pyrevit.*` | `pyrevit` | `pyrevit.export_rooms`, `pyrevit.rename_sheets` |
| `dynamo.*` | `dynamo` | `dynamo.run_graph`, `dynamo.set_comments_param_floors` |
| `flow.*` | `workflow` | `flow.create_walls_from_lines` |
| `composed.*` | `workflow` | `composed.create_wallandsphere` |

Rules:
- All lowercase
- Only letters, digits, dots, and underscores
- Minimum two dot-separated segments
- First segment must start with a letter

The JSON filename must match the tool name exactly: `revit.create_wall.json`.

---

## Adapter: C# / Revit API (`revit`)

Use the `revit` adapter for tools that call the Revit API directly via the C# add-in. This is the most common adapter for element creation, queries, and model modifications.

### How It Works

1. You define a tool JSON with `"adapter": "revit"`
2. When called, the dispatcher sends a `tool_call` message over the named pipe to the C# add-in
3. The C# add-in matches the tool name to a handler, executes the Revit API call, and returns the result
4. Model changes (created/modified/deleted element IDs) are tracked automatically

### Example: Read-Only Query Tool

```json
{
  "name": "revit.get_element_info",
  "adapter": "revit",
  "description": "Retrieves detailed information about a Revit element by its element ID, including category, type, parameters, and geometry bounds.",
  "version": "1.0.0",
  "tags": ["query", "elements"],
  "permissions": { "mode": "read" },
  "side_effects": [],
  "preconditions": [{ "check": "document_open" }],
  "parameters": {
    "type": "object",
    "properties": {
      "element_id": {
        "type": "integer",
        "description": "The Revit element ID to look up"
      },
      "include_parameters": {
        "type": "boolean",
        "description": "Whether to include all element parameters in the response",
        "default": true
      },
      "include_geometry": {
        "type": "boolean",
        "description": "Whether to include bounding box geometry",
        "default": false
      }
    },
    "required": ["element_id"]
  },
  "returns": {
    "type": "object",
    "properties": {
      "element_id": { "type": "integer" },
      "category": { "type": "string" },
      "type_name": { "type": "string" },
      "level": { "type": "string" },
      "parameters": { "type": "object" },
      "bounding_box": { "type": "object" }
    }
  },
  "examples": [
    {
      "description": "Get basic info about a wall element",
      "args": { "element_id": 12345 }
    }
  ]
}
```

### Example: Write Tool with MEP Parameters

```json
{
  "name": "revit.create_element",
  "adapter": "revit",
  "description": "Creates a generic element in the active Revit document between two points. Supports MEP elements (ducts, pipes, conduits, cable trays).",
  "version": "1.0.0",
  "tags": ["geometry", "mep", "elements"],
  "permissions": {
    "mode": "write",
    "categories": ["Ducts", "Pipes", "Conduits", "CableTrays"]
  },
  "side_effects": ["creates_elements"],
  "preconditions": [{ "check": "document_open" }],
  "parameters": {
    "type": "object",
    "properties": {
      "start_point": {
        "type": "array",
        "items": { "type": "number" },
        "minItems": 3,
        "maxItems": 3,
        "description": "Start point [X, Y, Z] in feet"
      },
      "end_point": {
        "type": "array",
        "items": { "type": "number" },
        "minItems": 3,
        "maxItems": 3,
        "description": "End point [X, Y, Z] in feet"
      },
      "type_name": {
        "type": "string",
        "description": "Name of the element type to use"
      },
      "level_name": {
        "type": "string",
        "description": "Name of the level. If omitted, uses the lowest level."
      },
      "diameter": {
        "type": "number",
        "minimum": 0,
        "description": "Diameter in feet (for round ducts/pipes)"
      }
    },
    "required": ["start_point", "end_point"]
  },
  "returns": {
    "type": "object",
    "properties": {
      "element_id": { "type": "integer" },
      "message": { "type": "string" }
    }
  }
}
```

### What You Need on the C# Side

The C# add-in receives a `tool_call` message with this shape:

```json
{
  "id": "uuid",
  "type": "tool_call",
  "payload": {
    "tool_name": "revit.create_element",
    "args": {
      "start_point": [0, 0, 10],
      "end_point": [20, 0, 10],
      "type_name": "Default"
    },
    "execution_mode": "headless"
  }
}
```

Your C# handler must return a `tool_result`:

```json
{
  "id": "uuid",
  "type": "tool_result",
  "payload": {
    "call_id": "<original message id>",
    "success": true,
    "data": {
      "element_id": 67890,
      "message": "Created duct element",
      "model_changes": {
        "created": [{ "id": 67890, "category": "Ducts", "type_name": "Default" }],
        "modified": [],
        "deleted": []
      }
    },
    "duration_ms": 250
  }
}
```

If the call fails:

```json
{
  "payload": {
    "call_id": "<original message id>",
    "success": false,
    "error": {
      "code": "ELEMENT_CREATION_FAILED",
      "message": "No valid duct type found with name 'NonExistent'"
    },
    "duration_ms": 15
  }
}
```

### Return Data: `model_changes`

If your tool creates, modifies, or deletes Revit elements, include a `model_changes` object in the result `data`. This is used by the UI History tab, workflow engine, and audit logs:

```json
"model_changes": {
  "created": [
    { "id": 12345, "category": "Walls", "type_name": "Generic - 200mm" }
  ],
  "modified": [
    { "id": 67890, "category": "Rooms" }
  ],
  "deleted": [11111]
}
```

---

## Adapter: pyRevit Scripts (`pyrevit`)

Use the `pyrevit` adapter for Python scripts that run inside Revit via IronPython. This is the best choice for batch operations, data export, parameter manipulation, and anything you'd normally write as a pyRevit script.

### How It Works

1. You define a tool JSON with `"adapter": "pyrevit"`
2. All pyRevit tools are normalized to the `pyrevit.run_script` command internally
3. The C# add-in executes the script using IronPython with model change tracking
4. Arguments are passed to the script as environment variables

### Tool Definition

```json
{
  "name": "pyrevit.export_rooms",
  "adapter": "pyrevit",
  "description": "Exports all room data (name, number, area, level) from the active document to a JSON file at the specified output path.",
  "version": "1.0.0",
  "tags": ["export", "rooms", "data"],
  "permissions": { "mode": "read" },
  "side_effects": ["file_io"],
  "preconditions": [{ "check": "document_open" }],
  "parameters": {
    "type": "object",
    "properties": {
      "script_path": {
        "type": "string",
        "description": "Absolute path to the Python script (.py)"
      },
      "arguments": {
        "type": "object",
        "description": "Key-value arguments passed as environment variables to the script",
        "additionalProperties": { "type": "string" }
      }
    },
    "required": ["script_path"]
  },
  "returns": {
    "type": "object",
    "properties": {
      "stdout": { "type": "string" },
      "stderr": { "type": "string" },
      "exit_code": { "type": "integer" }
    }
  },
  "examples": [
    {
      "description": "Export rooms to JSON",
      "args": {
        "script_path": "C:\\Scripts\\export_rooms.py",
        "arguments": { "OUTPUT_FORMAT": "json", "OUTPUT_PATH": "C:\\Temp\\rooms.json" }
      }
    }
  ]
}
```

### Writing the Python Script

Your script runs inside Revit's IronPython environment. You have access to:
- The Revit API via `clr` and `Autodesk.Revit.DB`
- The active document via `__revit__` (pyRevit's document reference)
- Arguments via environment variables (from the `arguments` dict)

```python
# export_rooms.py
import os
import json
import clr

clr.AddReference("RevitAPI")
from Autodesk.Revit.DB import FilteredElementCollector, BuiltInCategory

doc = __revit__.ActiveUIDocument.Document
output_path = os.environ.get("OUTPUT_PATH", "C:\\Temp\\rooms.json")
output_format = os.environ.get("OUTPUT_FORMAT", "json")

collector = FilteredElementCollector(doc)
rooms = collector.OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().ToElements()

data = []
for room in rooms:
    if room.Area > 0:
        data.append({
            "name": room.get_Parameter(BuiltInParameter.ROOM_NAME).AsString(),
            "number": room.Number,
            "area": room.Area,
            "level": room.Level.Name if room.Level else None,
        })

with open(output_path, "w") as f:
    json.dump(data, f, indent=2)

print("Exported {} rooms to {}".format(len(data), output_path))
```

### Key Points

- `script_path` must be an absolute Windows path to the `.py` file.
- `arguments` are string key-value pairs passed as environment variables. Keep values simple — use strings, not complex objects.
- `stdout` from the script is captured and returned to the caller.
- If the script writes to `stderr` or exits with a non-zero code, the result includes those.

### Wrapping an Existing pyRevit Extension

If you already have a pyRevit extension with scripts, you can expose them as tools without modification:

```json
{
  "name": "pyrevit.rename_sheets",
  "adapter": "pyrevit",
  "description": "Renames all sheets in the active document by applying a prefix and/or suffix pattern to the sheet name.",
  "permissions": { "mode": "write", "categories": ["Sheets"] },
  "side_effects": ["modifies_parameters"],
  "preconditions": [{ "check": "document_open" }],
  "parameters": {
    "type": "object",
    "properties": {
      "script_path": {
        "type": "string",
        "description": "Path to the rename_sheets.py script"
      },
      "arguments": {
        "type": "object",
        "properties": {
          "PREFIX": { "type": "string", "description": "Prefix to add to sheet names" },
          "SUFFIX": { "type": "string", "description": "Suffix to add to sheet names" }
        },
        "additionalProperties": { "type": "string" }
      }
    },
    "required": ["script_path"]
  }
}
```

---

## Adapter: Dynamo Graphs (`dynamo`)

Use the `dynamo` adapter for Dynamo `.dyn` graph files. This lets the AI or UI trigger Dynamo automations with parameterized inputs.

### How It Works

1. You define a tool JSON with `"adapter": "dynamo"`
2. All Dynamo tools are normalized to the `dynamo.run_graph` command internally
3. The C# add-in opens the `.dyn` file via the Dynamo API, sets input node values from the `inputs` dict, runs the graph, and returns outputs
4. The graph runs in the context of the active Revit document

### Tool Definition: Generic Graph Runner

The built-in `dynamo.run_graph` tool can run any graph:

```json
{
  "name": "dynamo.run_graph",
  "adapter": "dynamo",
  "description": "Runs a Dynamo graph (.dyn file) within the active Revit session. Input parameters can be passed to override Dynamo input nodes.",
  "version": "1.0.0",
  "tags": ["dynamo", "automation"],
  "permissions": { "mode": "write" },
  "side_effects": ["creates_elements", "modifies_elements"],
  "preconditions": [{ "check": "document_open" }],
  "parameters": {
    "type": "object",
    "properties": {
      "graph_path": {
        "type": "string",
        "description": "Absolute path to the Dynamo graph file (.dyn)"
      },
      "inputs": {
        "type": "object",
        "description": "Key-value pairs mapping Dynamo input node names to their values",
        "additionalProperties": true
      }
    },
    "required": ["graph_path"]
  },
  "returns": {
    "type": "object",
    "properties": {
      "outputs": { "type": "object" },
      "message": { "type": "string" }
    }
  },
  "examples": [
    {
      "description": "Run a graph that places furniture",
      "args": {
        "graph_path": "C:\\DynamoGraphs\\place_furniture.dyn",
        "inputs": { "Room Number": "101", "Furniture Type": "Desk" }
      }
    }
  ]
}
```

### Tool Definition: Specific Graph Wrapper

For a graph you use frequently, create a dedicated tool with a descriptive name and typed parameters:

```json
{
  "name": "dynamo.set_comments_param_floors",
  "adapter": "dynamo",
  "description": "Dynamo graph that sets the comments parameter on floor elements in the active Revit document.",
  "version": "1.0.0",
  "tags": ["dynamo", "parameters", "floors"],
  "permissions": {
    "mode": "write",
    "categories": ["Floors"]
  },
  "side_effects": ["modifies_parameters"],
  "parameters": {
    "type": "object",
    "properties": {
      "graph_path": {
        "type": "string",
        "description": "Path to the Dynamo graph file"
      },
      "inputs": {
        "type": "object",
        "description": "Key-value pairs mapping Dynamo input node names to their values",
        "additionalProperties": true
      }
    },
    "required": ["graph_path"]
  },
  "returns": {
    "type": "object",
    "properties": {
      "outputs": { "type": "object" },
      "message": { "type": "string" }
    }
  }
}
```

### Designing Your Dynamo Graph for Orchestrator

To make your graph work well as a tool:

1. **Use Input nodes** — Add `String Input`, `Number Input`, `Boolean Input`, or `Code Block` nodes as graph inputs. Their names become the keys in the `inputs` dict.

2. **Name inputs clearly** — The input node name is the key the caller uses. `"Room Number"` is better than `"String1"`.

3. **Keep outputs meaningful** — Output node values are captured and returned in the `outputs` dict.

4. **Avoid UI-dependent nodes** — Nodes that require manual selection (like `Select Model Element`) won't work in headless mode.

5. **Handle missing inputs** — If an input isn't provided, Dynamo uses the node's default value. Design your graph so defaults are reasonable.

**Example Dynamo input mapping:**

If your graph has these input nodes:
- `Room Number` (String Input)
- `Offset Height` (Number Slider, default: 0.0)
- `Create 3D` (Boolean, default: true)

Then the tool call would be:
```json
{
  "graph_path": "C:\\Graphs\\my_graph.dyn",
  "inputs": {
    "Room Number": "A-101",
    "Offset Height": 2.5,
    "Create 3D": false
  }
}
```

---

## Building Workflows

Workflows chain multiple tools into a single callable operation. They use the `workflow` adapter and define their steps declaratively in JSON.

### Basic Workflow

```json
{
  "name": "flow.create_walls_from_lines",
  "adapter": "workflow",
  "description": "Creates multiple walls from a list of line segments. Each line is defined by a start and end point.",
  "version": "1.0.0",
  "tags": ["workflow", "walls", "geometry"],
  "permissions": {
    "mode": "write",
    "categories": ["Walls"]
  },
  "side_effects": ["creates_elements"],
  "parameters": {
    "type": "object",
    "properties": {
      "lines": {
        "type": "array",
        "items": {
          "type": "object",
          "properties": {
            "start": {
              "type": "array",
              "items": { "type": "number" },
              "minItems": 3,
              "maxItems": 3
            },
            "end": {
              "type": "array",
              "items": { "type": "number" },
              "minItems": 3,
              "maxItems": 3
            }
          },
          "required": ["start", "end"]
        },
        "description": "Line segments with start/end [X, Y, Z] in feet"
      },
      "height": {
        "type": "number",
        "minimum": 0,
        "description": "Wall height in feet"
      }
    },
    "required": ["lines", "height"]
  }
}
```

### Workflow Steps

The `workflow.steps` array defines the execution sequence:

```json
"workflow": {
  "steps": [
    {
      "id": "step1",
      "tool": "revit.create_wall",
      "args": {
        "height": 10,
        "type_name": "Generic - 200mm"
      }
    },
    {
      "id": "step2",
      "tool": "dynamo.run_graph",
      "args": {
        "graph_path": "C:\\Graphs\\tag_walls.dyn"
      }
    }
  ]
}
```

Each step has:

| Field | Required | Description |
|-------|----------|-------------|
| `id` | No | Unique step identifier. Used for bindings. Auto-generated as `step1`, `step2`... if omitted. |
| `tool` | **Yes** | Name of the tool to call. Can be any registered tool including other workflows. |
| `args` | No | Static arguments passed to the tool. |
| `bindings` | No | Dynamic arguments resolved at runtime from workflow inputs or previous step results. |
| `guard` | No | Expression that must be truthy for the step to execute. |
| `on_failure` | No | What to do if the step fails: `"stop"` (default), `"skip"`, or `"retry"`. |
| `max_retries` | No | Number of retries if `on_failure` is `"retry"`. Default: `0`. |
| `timeout_ms` | No | Per-step timeout in milliseconds. |

### Bindings: Passing Data Between Steps

Bindings let you wire outputs from one step into inputs of the next:

```json
{
  "id": "create_wall",
  "tool": "revit.create_wall",
  "args": { "height": 10 }
},
{
  "id": "tag_wall",
  "tool": "revit.add_tag",
  "args": {},
  "bindings": {
    "element_id": "$steps.create_wall.data.element_id"
  }
}
```

**Binding expressions:**
- `$input.param_name` — References a workflow input parameter
- `$steps.step_id.data.field` — References a field from a previous step's result data
- `$steps.step_id.data.model_changes.created[0].id` — Nested path access

### Guards: Conditional Steps

Skip a step if a condition isn't met:

```json
{
  "id": "tag_wall",
  "tool": "revit.add_tag",
  "args": {},
  "bindings": {
    "element_id": "$steps.create_wall.data.element_id"
  },
  "guard": "$steps.create_wall.data.element_id != null"
}
```

### Workflow Governance

For workflows, use the `governance` block instead of top-level `permissions`/`side_effects`:

```json
"governance": {
  "author": "Jane Doe",
  "version": "1.0.0",
  "permission_mode": "write",
  "approval_required": false,
  "side_effects": ["creates_elements", "modifies_parameters"]
}
```

If you don't specify governance, it's auto-derived from the step tools — the workflow inherits the union of all step tools' side effects, and is `"write"` if any step is `"write"`.

---

## The Pipe Protocol

All communication between the Python server and the C# add-in uses length-prefixed JSON messages over a Windows named pipe.

### Message Format

```
[4-byte little-endian uint32: payload length] [UTF-8 JSON payload]
```

Max message size: 16 MB.

### Message Envelope

Every message has this structure:

```json
{
  "id": "unique-uuid",
  "type": "message_type",
  "timestamp": "2026-02-26T12:00:00.000Z",
  "payload": { }
}
```

### C# Handling a Tool Call

When the C# add-in receives a `tool_call` message:

1. Read the `payload.tool_name` to determine which handler to invoke
2. Read `payload.args` for the tool's input arguments
3. Read `payload.execution_mode` (`"headless"` or `"interactive"`)
4. Execute the Revit API operation
5. Send back a `tool_result` with `payload.call_id` set to the original message's `id`

```csharp
// Pseudocode for handling a tool_call
async Task HandleToolCall(PipeMessage message)
{
    var toolName = message.Payload.ToolName;    // e.g. "revit.create_wall"
    var args = message.Payload.Args;             // Dictionary<string, object>
    var mode = message.Payload.ExecutionMode;     // "headless" or "interactive"

    try
    {
        // Dispatch to your handler based on tool name
        var result = await ExecuteTool(toolName, args, mode);

        // Send result back
        await SendToolResult(new {
            call_id = message.Id,
            success = true,
            data = new {
                element_id = result.ElementId,
                message = result.Message,
                model_changes = result.ModelChanges
            },
            duration_ms = result.DurationMs
        });
    }
    catch (Exception ex)
    {
        await SendToolResult(new {
            call_id = message.Id,
            success = false,
            error = new { code = "EXECUTION_ERROR", message = ex.Message },
            duration_ms = 0
        });
    }
}
```

### Model Change Tracking

The C# add-in should track element changes during tool execution and include them in the result. This enables:
- The History tab in the UI to show what changed
- Workflows to reference created element IDs in later steps
- Audit logs to record the full impact of each operation

```csharp
// Track changes during a Revit transaction
using (var trans = new Transaction(doc, "Create Wall"))
{
    trans.Start();

    var wall = Wall.Create(doc, ...);

    trans.Commit();

    return new ToolResult {
        Success = true,
        Data = new {
            element_id = wall.Id.IntegerValue,
            model_changes = new {
                created = new[] {
                    new { id = wall.Id.IntegerValue, category = "Walls", type_name = wall.WallType.Name }
                },
                modified = Array.Empty<object>(),
                deleted = Array.Empty<object>()
            }
        }
    };
}
```

---

## Auto-Parsing Source Files

The Orchestrator can auto-generate a starter tool definition from a source file. This is useful for quick onboarding — you can import a file through the UI and get a skeleton JSON that you then refine.

### Supported Formats

| Extension | Adapter | What's extracted |
|-----------|---------|-----------------|
| `.dyn` | `dynamo` | Graph description, input nodes (from `NodeType: Input` or `ConcreteType: Symbol`) |
| `.py` | `pyrevit` | Module docstring, `main()`/`execute()`/`run()` function parameters |
| `.cs` | `revit` | XML doc `<summary>`, `Execute` method parameters, class name |

### From a Dynamo Graph

Given `place_furniture.dyn` with description "Places furniture in rooms" and input nodes "Room Number" and "Furniture Type":

```json
{
  "name": "dynamo.place_furniture",
  "adapter": "dynamo",
  "description": "Places furniture in rooms",
  "parameters": {
    "type": "object",
    "properties": {
      "graph_path": { "type": "string", "description": "Path to the Dynamo graph file" },
      "inputs": {
        "type": "object",
        "properties": {
          "Room Number": { "type": "string" },
          "Furniture Type": { "type": "string" }
        }
      }
    },
    "required": ["graph_path"]
  }
}
```

### From a Python Script

Given `export_rooms.py` with docstring and `def main(output_path, format="json")`:

```json
{
  "name": "pyrevit.export_rooms",
  "adapter": "pyrevit",
  "description": "Export rooms from the active Revit document.",
  "parameters": {
    "type": "object",
    "properties": {
      "script_path": { "type": "string", "description": "Path to the Python script" },
      "arguments": {
        "type": "object",
        "properties": {
          "output_path": { "type": "string" },
          "format": { "type": "string" }
        }
      }
    },
    "required": ["script_path"]
  }
}
```

### From a C# File

Given a class `WallCreator` with `Execute(Document doc, XYZ start, XYZ end, double height)`:

```json
{
  "name": "revit.wall_creator",
  "adapter": "revit",
  "description": "Creates a wall element.",
  "parameters": {
    "type": "object",
    "properties": {
      "start": { "type": "object" },
      "end": { "type": "object" },
      "height": { "type": "number" }
    },
    "required": ["start", "end", "height"]
  }
}
```

These auto-generated definitions are starting points. You should refine descriptions, add proper parameter types, add `permissions`/`side_effects`, and add `examples`.

---

## Validation and Error Handling

### Schema Validation

The dispatcher validates every tool call against the tool's `parameters` schema before execution. Invalid calls are rejected with detailed error messages:

```json
{
  "success": false,
  "error_code": "SCHEMA_VALIDATION_FAILED",
  "error_message": "Argument validation failed",
  "data": {
    "errors": [
      {
        "message": "'start_point' is a required property",
        "argument_path": "$.start_point",
        "expected": "required",
        "received_value": null,
        "received_type": "NoneType"
      }
    ]
  }
}
```

### Error Codes

| Code | Stage | Meaning |
|------|-------|---------|
| `TOOL_NOT_FOUND` | preflight | Tool name doesn't exist in the registry |
| `SCHEMA_VALIDATION_FAILED` | preflight | Arguments don't match the parameter schema |
| `PRECONDITION_FAILED` | preflight | A precondition check failed (e.g., no document open) |
| `ADAPTER_NOT_AVAILABLE` | dispatch | Adapter not registered or not connected |
| `PIPE_TIMEOUT` | adapter | C# add-in didn't respond within timeout |
| `PIPE_DISCONNECTED` | adapter | Named pipe connection was lost |
| `PYREVIT_SCRIPT_ERROR` | adapter | Python script threw an exception |
| `DYNAMO_EXECUTION_ERROR` | adapter | Dynamo graph execution failed |

---

## Hot Reload

When `watch_tools_dir` is enabled (default), the registry watches the tools directory with a filesystem watcher. Any changes are picked up automatically:

- **New file**: Tool is registered immediately
- **Modified file**: Tool definition is re-read and updated
- **Deleted file**: Tool is unregistered

This means you can iterate on tool definitions without restarting the server. Edit the JSON, save, and the tool is updated.

---

## Checklist: Registering a New Tool

1. **Choose your adapter**: `revit` for C# API calls, `pyrevit` for Python scripts, `dynamo` for Dynamo graphs
2. **Pick a name**: `{adapter}.{descriptive_name}` in lowercase with underscores
3. **Write the JSON definition** with at least `name`, `adapter`, `description`, `parameters`
4. **Save it** as `{tool_name}.json` in the tools directory
5. **Add permissions**: Set `mode` to `"read"` or `"write"`
6. **Declare side effects**: List what the tool changes
7. **Add preconditions**: Almost always include `{ "check": "document_open" }`
8. **Write examples**: Give the LLM concrete usage patterns
9. **Implement the C# handler** (for `revit` adapter) or ensure the script/graph exists at the path
10. **Test it**: Use the tool runner in the UI or the chat to verify it works
