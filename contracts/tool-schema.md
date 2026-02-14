# Tool Definition Schema

## Overview

Tools are defined as JSON files placed in `src/mcp-server/orchestrator/tools/`. Each file defines one tool that becomes available through the MCP server.

## Naming Convention

- File name: `{adapter}.{tool_name}.json` (e.g., `revit.create_wall.json`)
- Tool name in the file must match the file name (without `.json`)
- Handler file: `handlers/{adapter}_{tool_name}.py` (dots replaced with underscores)

## Schema

See `tool-definition.schema.json` for the formal JSON Schema.

### Required Fields

| Field         | Type   | Required | Description                                     |
|---------------|--------|----------|-------------------------------------------------|
| `name`        | string | Yes      | Unique tool identifier (e.g., `revit.create_wall`) |
| `adapter`     | string | Yes      | One of: `revit`, `pyrevit`, `dynamo`, `workflow` |
| `description` | string | Yes      | Human-readable description for LLM context      |
| `parameters`  | object | Yes      | JSON Schema for the tool's input arguments       |

### Optional Fields

| Field            | Type    | Description                                        |
|------------------|---------|----------------------------------------------------|
| `returns`        | object  | JSON Schema for the tool's output                  |
| `examples`       | array   | Example calls with expected inputs/outputs         |
| `version`        | string  | Semver version (e.g., `"1.0.0"`)                  |
| `author`         | string  | Author or owner of this tool                       |
| `tags`           | array   | String tags for categorization and filtering       |
| `deprecated`     | boolean | Whether this tool is deprecated                    |
| `superseded_by`  | string  | Name of the tool that replaces this one            |
| `permissions`    | object  | Read/write mode, categories, approval requirement  |
| `side_effects`   | array   | What the tool does to the model                    |
| `preconditions`  | array   | Requirements that must be met before execution     |
| `cost`           | object  | Duration estimate and caching hints                |
| `workflow`       | object  | Workflow definition with ordered steps             |
| `execution`      | object  | Execution mode configuration (interactive/headless) |
| `governance`     | object  | Governance metadata (author, tags, approval, etc.) |
| `metadata`       | object  | Arbitrary metadata (LLM review results, etc.)      |

### Permissions Object

| Field              | Type    | Default  | Description                            |
|--------------------|---------|----------|----------------------------------------|
| `mode`             | string  | `"write"` | `"read"` or `"write"`                 |
| `categories`       | array   | `[]`     | Revit categories affected              |
| `approval_required`| boolean | `false`  | Whether user must approve before run   |

### Side Effects

One or more of:
- `creates_elements` - Creates new Revit elements
- `modifies_elements` - Modifies existing Revit elements
- `deletes_elements` - Deletes Revit elements
- `modifies_parameters` - Changes element parameter values
- `changes_view` - Modifies views or visual settings
- `file_io` - Reads or writes files on disk

### Preconditions

Each precondition has a `check` field and optional `value`:
- `document_open` - A Revit document must be open
- `revit_version_gte` - Revit version must be >= `value` (e.g., `"2024"`)
- `worksharing_enabled` - Worksharing must be enabled

### Cost Object

| Field                  | Type    | Description                    |
|------------------------|---------|--------------------------------|
| `estimated_duration_ms`| integer | Expected execution time in ms  |
| `cacheable`            | boolean | Whether results can be cached  |
| `incremental`          | boolean | Whether supports partial runs  |

### Workflow Object

For tools with `adapter: "workflow"`, the `workflow` property defines a sequence of steps:

| Step Field    | Type    | Required | Description                                     |
|---------------|---------|----------|-------------------------------------------------|
| `id`          | string  | No       | Unique step identifier                          |
| `tool`        | string  | Yes      | Tool name to call                               |
| `args`        | object  | No       | Static arguments                                |
| `bindings`    | object  | No       | Dynamic args (expressions like `$steps.step1.data.element_id`) |
| `guard`       | string  | No       | Expression that must be truthy to run this step |
| `on_failure`  | string  | No       | `"stop"` (default), `"skip"`, or `"retry"`     |
| `max_retries` | integer | No       | Max retry count (default: 0)                    |
| `timeout_ms`  | integer | No       | Step timeout in milliseconds                    |

### Governance Object

For workflow tools, the `governance` property carries traceability and policy metadata:

| Field              | Type    | Default   | Description                             |
|--------------------|---------|-----------|---------------------------------------- |
| `author`           | string  | `""`      | Author or owner of the workflow          |
| `tags`             | array   | `[]`      | Tags for categorization                  |
| `version`          | string  | `"1.0.0"` | Governance semver version                |
| `permission_mode`  | string  | `"write"` | `"read"` or `"write"`                   |
| `approval_required`| boolean | `false`   | Whether user must approve before running |
| `side_effects`     | array   | `[]`      | Side effects (same enum as top-level)    |

### Execution Object

The `execution` field declares how a tool can be run. Tools that wrap applications with their own UI (e.g., Dynamo Player, pyRevit forms) may support both headless and interactive modes.

| Field              | Type   | Default        | Description                                                        |
|--------------------|--------|----------------|--------------------------------------------------------------------|
| `modes`            | array  | `["headless"]` | Supported execution modes: `"interactive"` and/or `"headless"`     |
| `interactive_hint` | string | `""`           | Describes what the native UI does, helping users choose a mode     |

**Mode semantics:**
- `headless` — The tool receives structured JSON inputs and runs without any native UI. This is the default and is always available.
- `interactive` — The tool opens its native UI (e.g., Dynamo opens with the graph loaded, pyRevit shows its own forms). The user interacts with the UI directly.

When a tool supports both modes, the Run dialog presents a mode picker. When only one mode is supported, the picker is hidden.

### Metadata Object

Free-form object for extensible metadata. Currently used for:
- `llm_review` — LLM review results (`summary`, `flags`, `suggestions`)

## Example Tool Definition

```json
{
  "name": "revit.create_wall",
  "adapter": "revit",
  "description": "Creates a wall in the active Revit document between two points with a specified height and wall type.",
  "version": "1.0.0",
  "tags": ["geometry", "walls"],
  "permissions": {
    "mode": "write",
    "categories": ["Walls"],
    "approval_required": false
  },
  "side_effects": ["creates_elements"],
  "preconditions": [
    { "check": "document_open" }
  ],
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
      "height": {
        "type": "number",
        "minimum": 0,
        "description": "Wall height in feet"
      },
      "wall_type": {
        "type": "string",
        "description": "Name of the wall type to use"
      }
    },
    "required": ["start_point", "end_point", "height"]
  },
  "examples": [
    {
      "description": "Create a 10-foot wall",
      "args": {
        "start_point": [0, 0, 0],
        "end_point": [10, 0, 0],
        "height": 10.0,
        "wall_type": "Generic - 200mm"
      }
    }
  ]
}
```
