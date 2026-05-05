"""Live audit log viewer for Revit Orchestrator.

Tails today's audit JSONL and prints each tool call in one colorized line:

    [12:34:01] START  chat   csharp.project_summary           (a3f2c7e1...)
    [12:34:02] OK     chat   csharp.project_summary  120ms
    [12:34:02] FAIL   chat   csharp.run_script        15ms  - File not found
    [12:34:05] END    completed (3 calls, 612+184 tok)

Run with `-f` / `--follow` to keep watching new entries.
Run without `-f` to print the last N entries (default 30) and exit.

Stdlib only.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
from datetime import datetime
from pathlib import Path
from typing import Any

# Enable ANSI escape sequences on Windows 10+ cmd/PowerShell.
os.system("")

# Resolve relative to this script: <repo>/utilities/watch_audit.py
# Override with --path or REVIT_ORCHESTRATOR_LOG_DIR if your layout differs.
_REPO_ROOT = Path(__file__).resolve().parent.parent
LOG_DIR = Path(os.environ.get("REVIT_ORCHESTRATOR_LOG_DIR")
               or _REPO_ROOT / "src" / "mcp-server" / "logs")

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


def latest_log() -> Path:
    """Return the most recently-modified audit-*.jsonl in LOG_DIR.

    The audit log file is named with the UTC date (timestamps inside use
    +00:00), so the file the server is writing to right now isn't always
    the local-date file. Picking by mtime sidesteps the timezone guess
    and also handles past-date forensics if the user passes an explicit
    --path.
    """
    if not LOG_DIR.is_dir():
        # Fall back to a probably-non-existent UTC name for the error message.
        from datetime import timezone
        return LOG_DIR / f"audit-{datetime.now(timezone.utc).strftime('%Y-%m-%d')}.jsonl"
    candidates = sorted(
        LOG_DIR.glob("audit-*.jsonl"),
        key=lambda p: p.stat().st_mtime,
        reverse=True,
    )
    if candidates:
        return candidates[0]
    from datetime import timezone
    return LOG_DIR / f"audit-{datetime.now(timezone.utc).strftime('%Y-%m-%d')}.jsonl"


def fmt_time(iso: str) -> str:
    """Convert the audit log's UTC ISO timestamp to local-time HH:MM:SS.

    Audit entries look like "2026-05-04T22:53:13.958276+00:00". We display
    in the user's local timezone so it matches the wall clock during a
    demo without mental subtraction.
    """
    if not iso:
        return "        "
    try:
        # Python 3.11+ handles "+00:00" suffix in fromisoformat.
        utc_dt = datetime.fromisoformat(iso)
        return utc_dt.astimezone().strftime("%H:%M:%S")
    except Exception:
        return iso[11:19] if len(iso) >= 19 else iso[:8]


def truncate(s: str, n: int) -> str:
    if len(s) <= n:
        return s
    return s[: n - 1] + "…"


def fmt_event(entry: dict[str, Any]) -> str:
    ts = fmt_time(entry.get("timestamp", ""))
    etype = entry.get("event_type", "")
    tool = entry.get("tool_name", "")
    origin = entry.get("origin", "?")
    cid = entry.get("correlation_id", "")[:8]
    dur = entry.get("duration_ms", 0)

    if etype == "started":
        return (
            f"{DIM}{ts}{RESET}  {BLUE}START {RESET}"
            f" {DIM}{origin:<6}{RESET} {tool:<40} {DIM}({cid}…){RESET}"
        )

    if etype == "completed":
        result = entry.get("result_summary", {}) or {}
        changes = (
            result.get("created_count", 0)
            + result.get("modified_count", 0)
            + result.get("deleted_count", 0)
        )
        suffix = f"  {GREEN}→ {changes} model change(s){RESET}" if changes else ""
        return (
            f"{DIM}{ts}{RESET}  {GREEN}OK    {RESET}"
            f" {DIM}{origin:<6}{RESET} {tool:<40} {DIM}{dur}ms{RESET}{suffix}"
        )

    if etype == "failed":
        err = entry.get("error", "")
        # First line of error, truncated.
        first = err.split("\n", 1)[0]
        return (
            f"{DIM}{ts}{RESET}  {RED}FAIL  {RESET}"
            f" {DIM}{origin:<6}{RESET} {tool:<40} {DIM}{dur}ms{RESET}"
            f"  {RED}{truncate(first, 90)}{RESET}"
        )

    if etype == "correlation_summary":
        outcome = entry.get("outcome", "")
        seq = entry.get("sequence", 0)
        usage = entry.get("total_usage", {}) or {}
        tok_in = usage.get("input_tokens", 0)
        tok_out = usage.get("output_tokens", 0)
        tok = f" {DIM}({tok_in}+{tok_out} tok){RESET}" if tok_in or tok_out else ""
        color = GREEN if outcome == "completed" else RED
        return (
            f"{DIM}{ts}{RESET}  {DIM}END   {RESET} "
            f"{color}{outcome}{RESET} {DIM}({seq} call(s)){RESET}{tok}\n"
            f"{DIM}─" * 1 + RESET
        )

    if etype == "tool_deleted":
        return (
            f"{DIM}{ts}{RESET}  {YELLOW}DEL   {RESET} "
            f"{DIM}{'-':<6}{RESET} {tool}"
        )

    # Anything else (e.g. workflow_completed, tool_renamed, tool_updated):
    # surface it dimly so we don't lose visibility but don't shout either.
    return (
        f"{DIM}{ts}  EVENT {RESET} "
        f"{DIM}{origin:<6}{RESET} {DIM}{etype}{RESET}  {tool}"
    )


def emit_existing(path: Path, n: int) -> None:
    if not path.exists():
        print(f"{YELLOW}No audit log yet at {path}{RESET}")
        return
    with path.open(encoding="utf-8") as f:
        lines = f.readlines()
    for raw in lines[-n:]:
        try:
            print(fmt_event(json.loads(raw)))
        except Exception:
            pass


def follow(path: Path) -> None:
    print(f"\n{CYAN}─── watching {path.name} for new events ───{RESET}\n")
    last_size = path.stat().st_size if path.exists() else 0
    pos = last_size
    while True:
        try:
            # The audit log rotates on UTC midnight; pick up the new file
            # whenever the latest-mtime file changes.
            cur = latest_log()
            if cur != path:
                path = cur
                pos = 0
                print(f"\n{CYAN}─── log rolled to {path.name} ───{RESET}\n")
            if not path.exists():
                time.sleep(1.0)
                continue
            stat = path.stat()
            if stat.st_size < pos:
                # File was truncated/replaced — restart from top.
                pos = 0
            if stat.st_size > pos:
                with path.open(encoding="utf-8") as f:
                    f.seek(pos)
                    for line in f:
                        try:
                            print(fmt_event(json.loads(line)))
                        except Exception:
                            pass
                    pos = f.tell()
            else:
                time.sleep(0.5)
        except KeyboardInterrupt:
            raise
        except Exception as e:
            print(f"{RED}watch error: {e}{RESET}")
            time.sleep(1.0)


def main() -> None:
    p = argparse.ArgumentParser(description="Live audit log viewer")
    p.add_argument("-f", "--follow", action="store_true",
                   help="keep watching for new events")
    p.add_argument("-n", type=int, default=30,
                   help="number of recent entries to show before following (default 30)")
    p.add_argument("--path", help="override audit log path")
    args = p.parse_args()

    path = Path(args.path) if args.path else latest_log()

    print(f"{BOLD}Revit Orchestrator audit log{RESET}  {DIM}{path}{RESET}\n")
    emit_existing(path, args.n)

    if not args.follow:
        return

    try:
        follow(path)
    except KeyboardInterrupt:
        print(f"\n{DIM}stopped.{RESET}")


if __name__ == "__main__":
    main()
