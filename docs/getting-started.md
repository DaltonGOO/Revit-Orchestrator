# Getting Started

## Prerequisites

- **Python 3.11+** with pip
- **.NET 8 SDK** (for building the Revit add-in)
- **Autodesk Revit 2025 or 2026**
- An API key for Claude (Anthropic) or OpenAI

## Setup

### 1. Clone the Repository

```bash
git clone https://github.com/your-org/Revit-Orchestrator.git
cd Revit-Orchestrator
```

### 2. Install the Python MCP Server

```bash
cd src/mcp-server
pip install -e ".[dev]"
```

### 3. Configure Environment

Set API keys as environment variables:

```bash
# For Claude
set ANTHROPIC_API_KEY=sk-ant-...

# For OpenAI
set OPENAI_API_KEY=sk-...
set ORCHESTRATOR_LLM_PROVIDER=openai
```

### 4. Build the Revit Add-in

```bash
cd src/revit-addin
dotnet build -p:RevitVersion=2025 -c Release
```

If Revit is not installed at the default location, specify the API path:

```bash
dotnet build -p:RevitVersion=2025 -p:RevitApiPath="C:\path\to\RevitAPI"
```

### 5. Install the Add-in

Copy the build output and `.addin` manifest to Revit's add-in folder:

```
%APPDATA%\Autodesk\Revit\Addins\2025\
```

Required files:
- `RevitOrchestrator.dll`
- `RevitOrchestrator.addin`
- `System.Text.Json.dll` (and any other dependencies)

### 6. Start the MCP Server

```bash
cd src/mcp-server
python -m orchestrator.server
```

Or use the MCP inspector for testing:

```bash
mcp dev orchestrator/server.py
```

### 7. Launch Revit

Open Revit. The Orchestrator panel should appear in the ribbon. Click it to open the chat panel.

## Verifying the Setup

1. **MCP Server**: Run `mcp dev orchestrator/server.py` and verify all tools appear
2. **Pipe Connection**: The chat panel status indicator should turn green when connected
3. **Test Command**: Type "Get info about element 12345" in the chat panel

## Configuration

| Environment Variable | Default | Description |
|---------------------|---------|-------------|
| `ORCHESTRATOR_PIPE_NAME` | `\\.\pipe\RevitOrchestrator` | Named pipe path |
| `ORCHESTRATOR_LLM_PROVIDER` | `claude` | LLM provider (`claude` or `openai`) |
| `ANTHROPIC_API_KEY` | — | Anthropic API key |
| `ANTHROPIC_MODEL` | `claude-sonnet-4-20250514` | Claude model ID |
| `OPENAI_API_KEY` | — | OpenAI API key |
| `OPENAI_MODEL` | `gpt-4o` | OpenAI model ID |
| `ORCHESTRATOR_TOOLS_DIR` | `orchestrator/tools` | Path to tool definitions |
| `ORCHESTRATOR_PIPE_TIMEOUT` | `30` | Pipe call timeout in seconds |
| `ORCHESTRATOR_WATCH_TOOLS` | `true` | Hot-reload tool definitions |
