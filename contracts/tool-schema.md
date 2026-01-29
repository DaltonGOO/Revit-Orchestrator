# Tool Definition Schema

## Overview

Tools are defined as JSON files placed in `src/mcp-server/orchestrator/tools/`. Each file defines one tool that becomes available through the MCP server.

## Naming Convention

- File name: `{adapter}.{tool_name}.json` (e.g., `revit.create_wall.json`)
- Tool name in the file must match the file name (without `.json`)
- Handler file: `handlers/{adapter}_{tool_name}.py` (dots replaced with underscores)

## Schema

See `tool-definition.schema.json` for the formal JSON Schema.

### Fields

| Field         | Type   | Required | Description                                     |
|---------------|--------|----------|-------------------------------------------------|
| `name`        | string | Yes      | Unique tool identifier (e.g., `revit.create_wall`) |
| `adapter`     | string | Yes      | One of: `revit`, `pyrevit`, `dynamo`, `workflow` |
| `description` | string | Yes      | Human-readable description for LLM context      |
| `parameters`  | object | Yes      | JSON Schema for the tool's input arguments       |
| `returns`     | object | No       | JSON Schema for the tool's output                |
| `examples`    | array  | No       | Example calls with expected inputs/outputs       |

## Example Tool Definition

```json
{
  "name": "revit.create_wall",
  "adapter": "revit",
  "description": "Creates a wall in the active Revit document between two points with a specified height and wall type.",
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
