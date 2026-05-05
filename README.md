# Revit Orchestrator

> Connect LLMs to Autodesk Revit through an MCP server, named-pipe IPC, and a multi-adapter command dispatcher.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Revit](https://img.shields.io/badge/Revit-2025%20%7C%202026-1f6feb)](https://www.autodesk.com/products/revit)
[![Python](https://img.shields.io/badge/Python-3.11%2B-3776ab)](https://www.python.org/)
[![.NET](https://img.shields.io/badge/.NET-8.0-512bd4)](https://dotnet.microsoft.com/)

**Project site:** [daltongoo.github.io/Revit-Orchestrator](https://daltongoo.github.io/Revit-Orchestrator/)

Revit Orchestrator is an open-source bridge between Large Language Models and Autodesk Revit. It exposes Revit automation — C# Revit API commands, pyRevit scripts, Dynamo graphs, and composed multi-step workflows — as a unified catalog of schema-validated tools that any MCP-aware AI assistant (Claude, OpenAI, etc.) can call.

## What it does

- **Talk to Revit.** Ask an LLM to query elements, place families, modify parameters, or run a workflow — it calls registered tools through the Model Context Protocol with typed JSON Schemas.
- **Wrap what you have.** A single JSON file registers a tool and routes it to the right adapter: C# command, pyRevit script, Dynamo graph, or workflow.
- **Stay safe.** Every call is validated against JSON Schema, gated by permissions and preconditions, executed in a Revit transaction on the main thread via `ExternalEvent`, and logged with model-change deltas.
- **Extend without rebuilding.** A filesystem watcher hot-reloads tool definitions — drop a JSON file in `tools/` and the tool appears in the catalog immediately.

## Architecture

```
┌──────────────────┐     MCP      ┌──────────────────┐  Named Pipe  ┌──────────────────┐
│   LLM Provider   │◄────────────►│  Python MCP      │◄────────────►│  C# Revit        │
│  (Claude/OpenAI) │   (stdio)    │  Server          │   (JSON)     │  Add-in          │
└──────────────────┘              └──────────────────┘              └──────────────────┘
                                         │                                  │
                                  Adapters: revit                    ExternalEvent bridge
                                          pyrevit                    Revit API on main thread
                                          dynamo
                                          workflow
```

Read the full overview at [docs/architecture.html](https://daltongoo.github.io/Revit-Orchestrator/architecture.html).

## Quick start

### Prerequisites
- Python 3.11+ with pip
- .NET 8 SDK
- Autodesk Revit 2025 or 2026
- An API key for Claude (Anthropic) or OpenAI

### Install

```bash
git clone https://github.com/DaltonGOO/Revit-Orchestrator.git
cd Revit-Orchestrator

# 1. Python MCP server
cd src/mcp-server
pip install -e ".[dev]"

# 2. Revit add-in
cd ../revit-addin
dotnet build -p:RevitVersion=2025 -c Release
```

### Configure your LLM

```bash
# Claude
set ANTHROPIC_API_KEY=sk-ant-...

# or OpenAI
set OPENAI_API_KEY=sk-...
set ORCHESTRATOR_LLM_PROVIDER=openai
```

### Run

```bash
cd src/mcp-server
python -m orchestrator.server
```

Then open Revit and click the **Orchestrator** panel in the ribbon.

The full setup guide is at [docs/getting-started.html](https://daltongoo.github.io/Revit-Orchestrator/getting-started.html).

### Packaged install (no toolchain required)

To install on a machine without the .NET SDK or Python, build a self-contained
zip on a dev machine and copy it across:

```powershell
# On the dev machine — produces dist\RevitOrchestrator-vYYYYMMDD-Revit2025.zip
.\package.ps1 -RevitVersion 2025

# On the target machine — unzip, then:
.\install.ps1 -RevitVersion 2025
```

`package.ps1` bundles the C# add-in DLLs, a PyInstaller-compiled
`orchestrator.exe`, and the `tools/` folder (so the sample C#/pyRevit/Dynamo
tools work out of the box). `install.ps1` copies the bundle into
`%APPDATA%\Autodesk\Revit\Addins\<RevitVersion>\` and writes the `.addin`
manifest. After install, set your `ANTHROPIC_API_KEY` (or `OPENAI_API_KEY`) and
launch Revit.

## Adding a tool

A tool is a JSON file in `src/mcp-server/orchestrator/tools/`:

```json
{
  "name": "revit.create_wall",
  "adapter": "revit",
  "description": "Creates a wall in the active document between two points with a given height.",
  "permissions": { "mode": "write", "categories": ["Walls"] },
  "side_effects": ["creates_elements"],
  "preconditions": [{ "check": "document_open" }],
  "parameters": {
    "type": "object",
    "properties": {
      "start_point": { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
      "end_point":   { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
      "height":      { "type": "number", "minimum": 0 }
    },
    "required": ["start_point", "end_point", "height"]
  }
}
```

For more, see [Adding tools](https://daltongoo.github.io/Revit-Orchestrator/adding-tools.html) or the full [Tool developer guide](https://daltongoo.github.io/Revit-Orchestrator/tool-developer-guide.html).

## Documentation

- [Project site](https://daltongoo.github.io/Revit-Orchestrator/) — landing page
- [Architecture](https://daltongoo.github.io/Revit-Orchestrator/architecture.html)
- [Getting started](https://daltongoo.github.io/Revit-Orchestrator/getting-started.html)
- [Adding tools](https://daltongoo.github.io/Revit-Orchestrator/adding-tools.html)
- [Tool developer guide](https://daltongoo.github.io/Revit-Orchestrator/tool-developer-guide.html)
- [MCP connections](https://daltongoo.github.io/Revit-Orchestrator/mcp-connections.html) — connect to external MCP servers

Source markdown for the docs lives in [`docs/`](docs/) alongside the rendered HTML.

## Repository layout

```
src/
├── mcp-server/      Python MCP server, dispatcher, registry, adapters
└── revit-addin/     C# Revit add-in (multi-version: 2025, 2026)
contracts/           Pipe protocol, tool-definition schema, error codes
docs/                GitHub Pages site + source markdown
```

## License

[MIT](LICENSE) © The BIM Coordinator
