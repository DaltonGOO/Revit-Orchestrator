# Revit Orchestrator utilities

Two small log viewers for traceability — what's happening in your tool, in
real time, without opening a JSON file in a text editor.

## `watch_audit` — every tool call as it happens

Tails today's `audit-YYYY-MM-DD.jsonl` and prints one colorized line per
event. Quick scan tells you: which tool, who called it (chat or manual),
how long it took, success/fail, and how many model changes it produced.

Sample output:

```
12:34:01  START   chat   csharp.project_summary           (a3f2c7e1…)
12:34:02  OK      chat   csharp.project_summary  120ms
12:34:02  START   chat   csharp.warnings_summary          (a3f2c7e1…)
12:34:02  OK      chat   csharp.warnings_summary  35ms
12:34:03  END     completed (2 calls, 612+184 tok)
```

**Usage**

```
python watch_audit.py            # last 30 events, exit
python watch_audit.py --follow   # last 30 + keep watching
python watch_audit.py -f -n 100  # last 100 + keep watching
```

Or just **double-click `watch_audit.bat`**.

## `watch_server` — what the server is doing

Tails `orchestrator-startup.log` (the Python server's stdout/stderr) with
category badges so the noisy stuff dims and the important stuff pops:

| badge | meaning |
|-------|---------|
| `READY` | `ORCHESTRATOR_READY` printed on stdout — server is live |
| `PIPE`  | pipe server start/stop, client connect/disconnect |
| `TOOL`  | tool registry events: load, register, hot-reload, schema errors |
| `LLM`   | LLM router initialised, **the tool catalog sent to Claude** |
| `MCP`   | external MCP client connections (filesystem, etc.) |
| `ORPH`  | orphan-cleanup actions on startup |
| `ERR`   | exceptions, tracebacks, anything labelled `[ERROR]` |
| `WARN`  | `[WARNING]` lines |
| `EXIT`  | shutdown events |

**Usage**

```
python watch_server.py
python watch_server.py --follow
```

Or just **double-click `watch_server.bat`**.

## Suggested setup during a demo

Open both viewers side-by-side in two PowerShell / cmd windows:

```
+----------------+  +----------------+
| watch_audit -f |  | watch_server -f|
+----------------+  +----------------+
```

When you fire a chat prompt, you'll see in real time:
1. Which tools the LLM picked (audit window — `START` lines)
2. How long each ran (audit window — `OK` / `FAIL` lines)
3. The tool catalog the LLM was given that turn (server window — `LLM tool catalog`)
4. Any pipe / process issues (server window — `PIPE` / `ERR`)

If something silently goes wrong (e.g. tool not found, server died, stale
config), one of these two views will tell you immediately.

## Notes

- Stdlib only — no `pip install` needed. Uses ANSI escape codes; works in
  Windows 10/11 cmd, PowerShell, and Windows Terminal.
- Paths to the logs are hardcoded for the default install. Override with
  `--path /some/other/file` if needed.
- Both scripts roll over correctly when the audit log changes day.
