"""Live server log viewer for Revit Orchestrator.

Tails orchestrator-startup.log and prints each meaningful event with a
category badge plus color, e.g.:

    12:34:01  PIPE   Pipe server started on \\\\.\\pipe\\RevitOrchestrator
    12:34:02  TOOL   Loaded tool: csharp.project_summary
    12:34:05  LLM    LLM tool catalog (14 tools): csharp_count_walls, ...
    12:34:08  ERR    SCRIPT_LOAD_ERROR: ImportException: No module named 'io'

Routine noise is dimmed; errors are red. Run with `-f` to follow.

Stdlib only.
"""

from __future__ import annotations

import argparse
import os
import re
import sys
import time
from pathlib import Path

os.system("")  # enable ANSI on Windows

# Default to the Revit 2025 user add-in install location. Override via
# --path or REVIT_ORCHESTRATOR_SERVER_LOG if your install differs (other
# Revit version, all-users install, etc.).
_DEFAULT_LOG = (
    Path(os.environ["APPDATA"])
    / "Autodesk" / "Revit" / "Addins" / "2025"
    / "RevitOrchestrator" / "orchestrator-startup.log"
) if os.environ.get("APPDATA") else Path("orchestrator-startup.log")
LOG_PATH = Path(os.environ.get("REVIT_ORCHESTRATOR_SERVER_LOG") or _DEFAULT_LOG)

# ─── colors ─────────────────────────────────────────────────────────────
RESET = "\x1b[0m"
DIM = "\x1b[2m"
BOLD = "\x1b[1m"
RED = "\x1b[91m"
GREEN = "\x1b[92m"
YELLOW = "\x1b[93m"
BLUE = "\x1b[94m"
CYAN = "\x1b[96m"
MAGENTA = "\x1b[95m"

# ─── classification ─────────────────────────────────────────────────────
# (regex, color, badge). First match wins. Order matters.
RULES: list[tuple[re.Pattern[str], str, str]] = [
    # Errors first so they always win.
    (re.compile(r"\[ERROR\]|Traceback|Exception|Could not load|Failed", re.I), RED, "ERR  "),
    (re.compile(r"\[WARNING\]", re.I), YELLOW, "WARN "),

    # Pipe lifecycle.
    (re.compile(r"Pipe server started", re.I), BLUE, "PIPE "),
    (re.compile(r"Pipe server (stopped|loop exited)", re.I), YELLOW, "PIPE "),
    (re.compile(r"New pipe client|Pipe client (connected|disconnect)", re.I), BLUE, "PIPE "),

    # Server lifecycle.
    (re.compile(r"ORCHESTRATOR_READY", re.I), GREEN, "READY"),
    (re.compile(r"Shutdown complete|shutdown event", re.I), YELLOW, "EXIT "),
    (re.compile(r"Orphan cleanup", re.I), CYAN, "ORPH "),
    (re.compile(r"LLM router initialized|LLM provider", re.I), CYAN, "LLM  "),

    # LLM tool catalog (the new logging we added during diagnostics).
    (re.compile(r"LLM tool catalog", re.I), MAGENTA, "LLM  "),
    (re.compile(r"Calling LLM", re.I), MAGENTA, "LLM  "),

    # Tool registration / hot-reload.
    (re.compile(r"Registered MCP tool|Loaded tool:|Registered tool:|Registry loaded", re.I), GREEN, "TOOL "),
    (re.compile(r"Schema errors", re.I), RED, "TOOL "),
    (re.compile(r"Watching tools directory|hot-reload", re.I), DIM + GREEN, "TOOL "),

    # MCP external connections.
    (re.compile(r"Connected to '|MCP client session", re.I), CYAN, "MCP  "),
]

# Strip the verbose log prefix down to (timestamp, message).
PREFIX = re.compile(
    r"""^
    \[(\d{4}-\d{2}-\d{2}\s\d{2}:\d{2}:\d{2})\]   # outer timestamp
    \s\[(?:stderr|stdout)\]\s
    (?:(\d{4}-\d{2}-\d{2}\s\d{2}:\d{2}:\d{2},\d+)\s)?   # inner timestamp (optional)
    (?:\[(?:INFO|ERROR|WARNING|DEBUG)\]\s)?               # log level (optional)
    (.*)$
    """,
    re.VERBOSE,
)


def classify(line: str) -> tuple[str, str]:
    for pattern, color, badge in RULES:
        if pattern.search(line):
            return color, badge
    return DIM, "     "


def fmt_line(line: str) -> str | None:
    """Return formatted output, or None to drop the line entirely."""
    line = line.rstrip("\n").rstrip("\r")
    if not line.strip():
        return None

    m = PREFIX.match(line)
    if m:
        outer_ts = m.group(1)
        msg = m.group(3)
    else:
        outer_ts = "                 "
        msg = line

    short_ts = outer_ts[11:] if len(outer_ts) >= 11 else outer_ts  # HH:MM:SS

    color, badge = classify(line)

    # Dim category gets dim message too.
    if color == DIM:
        return f"{DIM}{short_ts}  {badge}  {msg[:140]}{RESET}"
    return f"{DIM}{short_ts}{RESET}  {color}{badge}{RESET}  {msg[:160]}"


def emit_existing(path: Path, n: int) -> None:
    if not path.exists():
        print(f"{YELLOW}No server log yet at {path}{RESET}")
        return
    with path.open(encoding="utf-8", errors="replace") as f:
        lines = f.readlines()
    for raw in lines[-n:]:
        out = fmt_line(raw)
        if out is not None:
            print(out)


def follow(path: Path) -> None:
    print(f"\n{CYAN}─── watching {path.name} for new events ───{RESET}\n")
    pos = path.stat().st_size if path.exists() else 0
    while True:
        try:
            if not path.exists():
                time.sleep(1.0)
                continue
            stat = path.stat()
            if stat.st_size < pos:
                # File was rotated / truncated.
                pos = 0
            if stat.st_size > pos:
                with path.open(encoding="utf-8", errors="replace") as f:
                    f.seek(pos)
                    for line in f:
                        out = fmt_line(line)
                        if out is not None:
                            print(out)
                    pos = f.tell()
            else:
                time.sleep(0.5)
        except KeyboardInterrupt:
            raise
        except Exception as e:
            print(f"{RED}watch error: {e}{RESET}")
            time.sleep(1.0)


def main() -> None:
    p = argparse.ArgumentParser(description="Live server log viewer")
    p.add_argument("-f", "--follow", action="store_true",
                   help="keep watching for new events")
    p.add_argument("-n", type=int, default=80,
                   help="number of recent lines to show before following (default 80)")
    p.add_argument("--path", help="override log path")
    args = p.parse_args()

    path = Path(args.path) if args.path else LOG_PATH

    print(f"{BOLD}Revit Orchestrator server log{RESET}  {DIM}{path}{RESET}\n")
    emit_existing(path, args.n)

    if not args.follow:
        return

    try:
        follow(path)
    except KeyboardInterrupt:
        print(f"\n{DIM}stopped.{RESET}")


if __name__ == "__main__":
    main()
