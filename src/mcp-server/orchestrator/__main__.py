"""CLI entry point for ``python -m orchestrator``."""

from __future__ import annotations

import argparse
import logging
import sys


def main() -> None:
    parser = argparse.ArgumentParser(
        prog="orchestrator",
        description="Revit Orchestrator MCP server",
    )
    parser.add_argument(
        "--mode",
        choices=["pipe", "mcp"],
        default="pipe",
        help="Run mode: 'pipe' (default) blocks on named pipe; 'mcp' runs MCP stdio transport",
    )
    parser.add_argument(
        "--log-dir",
        default=None,
        help="Override log directory (default: %%LOCALAPPDATA%%/RevitOrchestrator/logs)",
    )
    args = parser.parse_args()

    # Configure logging BEFORE importing server (prevents premature handlers)
    from .logging_config import configure_logging

    log_dir = configure_logging(args.log_dir)
    _log = logging.getLogger(__name__)

    # Now import and initialize the server
    from . import server

    try:
        server.init()
    except Exception:
        _log.exception("FATAL: Server initialization failed")
        sys.exit(1)

    # Signal readiness to the parent process (C# watches for this line)
    print("ORCHESTRATOR_READY", flush=True)

    try:
        if args.mode == "pipe":
            server.run_pipe_only()
        else:
            server.run_mcp()
    except Exception:
        _log.exception("FATAL: run loop crashed")
    finally:
        _log.info("Main loop exited, calling shutdown()")
        server.shutdown()


if __name__ == "__main__":
    main()
