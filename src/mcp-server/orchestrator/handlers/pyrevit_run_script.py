"""Handler for pyrevit.run_script — invokes pyRevit CLI scripts."""

from __future__ import annotations

import asyncio
import os
from typing import Any

from ..dispatcher.result import ToolResult


async def execute(args: dict[str, Any], **kwargs: Any) -> ToolResult:
    """Execute a pyRevit script via the CLI.

    Runs the script as a subprocess and captures stdout/stderr.
    """
    script_path = args["script_path"]
    script_args = args.get("arguments", {})

    # Build environment with script arguments
    env = os.environ.copy()
    for key, value in script_args.items():
        env[key] = str(value)

    try:
        proc = await asyncio.create_subprocess_exec(
            "pyrevit",
            "run",
            script_path,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            env=env,
        )
        stdout, stderr = await asyncio.wait_for(proc.communicate(), timeout=120)

        stdout_str = stdout.decode("utf-8", errors="replace") if stdout else ""
        stderr_str = stderr.decode("utf-8", errors="replace") if stderr else ""

        if proc.returncode != 0:
            return ToolResult.fail(
                "PYREVIT_SCRIPT_ERROR",
                f"Script exited with code {proc.returncode}: {stderr_str}",
            )

        return ToolResult.ok({
            "stdout": stdout_str,
            "stderr": stderr_str,
            "exit_code": proc.returncode,
        })

    except FileNotFoundError:
        return ToolResult.fail(
            "PYREVIT_SCRIPT_ERROR",
            "pyRevit CLI not found. Ensure pyRevit is installed and on PATH.",
        )
    except asyncio.TimeoutError:
        return ToolResult.fail(
            "PYREVIT_SCRIPT_ERROR",
            "Script execution timed out after 120 seconds.",
        )
