"""Adapter that runs Dynamo graphs."""

from __future__ import annotations

import asyncio
import logging
import uuid
from typing import Any

from .base import BaseAdapter
from ..dispatcher.result import ToolResult
from ..pipe.protocol import make_dynamo_open_run_dialog_request

logger = logging.getLogger(__name__)


# Dynamo tools that don't need Revit / Dynamo at all — pure file-read metadata.
# These run their Python handler directly instead of going over the pipe to C#.
_LOCAL_DYNAMO_TOOLS = frozenset({
    "dynamo.describe_graph",
})

# How long to wait for the user to interact with the Run Graph dialog. Long
# enough for the user to fill in a form, short enough that a stuck or invisible
# dialog fails fast instead of looking like a permanent hang. The user can
# also click Cancel in the chat UI to abort earlier.
_INTERACTIVE_DIALOG_TIMEOUT_SECONDS = 5 * 60  # 5 minutes

# How long Python waits on the pipe for a Dynamo run to complete. The C# side
# already enforces a 5-minute internal timeout on the actual graph evaluation
# (RunDynamoGraphCommand.RunTimeout) — this just needs to be a bit longer so
# the legitimate completion isn't pre-empted as a pipe timeout, which would
# show up in History as a spurious failure even though the graph actually ran.
_DYNAMO_RUN_PIPE_TIMEOUT_SECONDS = 6 * 60  # 6 minutes


class DynamoAdapter(BaseAdapter):
    """Executes Dynamo-related tools.

    Most Dynamo tools (e.g. ``dynamo.run_graph``) are sent over the pipe to the
    C# Revit add-in, which drives the Dynamo API. A small set of *metadata*
    tools (e.g. ``dynamo.describe_graph``) only read the .dyn file and are run
    in-process by their Python handler — no Revit needed.
    """

    def __init__(self) -> None:
        self._revit_adapter: Any | None = None
        self._connection: Any | None = None

    @property
    def name(self) -> str:
        return "dynamo"

    def set_revit_adapter(self, revit_adapter: Any) -> None:
        """Set the Revit adapter used to communicate with the add-in."""
        self._revit_adapter = revit_adapter

    def set_connection(self, connection: Any) -> None:
        """Set the named-pipe connection used for direct (non-tool_call) messages.

        Required by ``dynamo.run_graph_interactive`` which sends a custom
        ``dynamo_open_run_dialog_request`` message rather than a regular tool
        call.
        """
        self._connection = connection

    async def execute(
        self, tool_name: str, args: dict[str, Any], handler: Any
    ) -> ToolResult:
        # Metadata tools: run the Python handler directly.
        if tool_name in _LOCAL_DYNAMO_TOOLS:
            if handler is None:
                return ToolResult.fail(
                    "HANDLER_NOT_FOUND",
                    f"No Python handler module for {tool_name}",
                )
            try:
                return await handler.execute(args)
            except Exception as e:
                logger.exception("Dynamo metadata tool error for %s", tool_name)
                return ToolResult.fail("DYNAMO_EXECUTION_ERROR", str(e))

        # Interactive dialog tool: send a custom pipe message and block until
        # the user clicks Run / Cancel in the C# dialog.
        if tool_name == "dynamo.run_graph_interactive":
            return await self._execute_interactive(args)

        # Execution tools: route over the pipe to C#.
        if self._revit_adapter is None:
            return ToolResult.fail(
                "ADAPTER_NOT_AVAILABLE",
                "Revit add-in is not connected",
            )

        try:
            # All Dynamo execution tools map to run_graph on the C# side
            normalized_tool = "dynamo.run_graph"
            return await self._revit_adapter.execute(
                normalized_tool,
                args,
                handler,
                pipe_timeout=_DYNAMO_RUN_PIPE_TIMEOUT_SECONDS,
            )
        except Exception as e:
            logger.exception("Dynamo execution error for %s", tool_name)
            return ToolResult.fail("DYNAMO_EXECUTION_ERROR", str(e))

    async def _execute_interactive(self, args: dict[str, Any]) -> ToolResult:
        """Open the Run Graph dialog in Revit and wait for the user's result."""
        if self._connection is None or not getattr(self._connection, "connected", False):
            return ToolResult.fail(
                "ADAPTER_NOT_AVAILABLE",
                "Revit add-in is not connected",
            )

        graph_path = args.get("graph_path", "")
        suggested = args.get("suggested_inputs") or {}

        # Use a single UUID for both the message envelope id and the call_id
        # in the payload so the C# round-trip can echo it back and the pipe's
        # response router (keyed by call_id) finds our pending future.
        call_id = str(uuid.uuid4())
        msg = make_dynamo_open_run_dialog_request(
            graph_path=graph_path,
            suggested_inputs=suggested,
            call_id=call_id,
        )
        msg["id"] = call_id

        try:
            response = await self._connection.send_and_wait(
                msg, timeout=_INTERACTIVE_DIALOG_TIMEOUT_SECONDS
            )
        except asyncio.TimeoutError:
            return ToolResult.fail(
                "DYNAMO_DIALOG_TIMEOUT",
                "Run Graph dialog was not closed within the wait window",
            )
        except ConnectionError:
            return ToolResult.fail(
                "PIPE_DISCONNECTED",
                "Lost connection to Revit add-in while the Run Graph dialog was open",
            )

        payload = response.get("payload", {}) if isinstance(response, dict) else {}
        status = payload.get("status", "error")

        if status == "ran":
            return ToolResult.ok({
                "status": "ran",
                "inputs_used": payload.get("inputs_used", {}),
                "result": payload.get("result", {}),
            })
        if status == "cancelled":
            return ToolResult.ok({
                "status": "cancelled",
                "message": "User closed the Run Graph dialog without running.",
            })
        return ToolResult.fail(
            "DYNAMO_DIALOG_ERROR",
            payload.get("error") or "Run Graph dialog returned an error",
        )

    async def is_available(self) -> bool:
        # The adapter itself is always available — its metadata tools work
        # without Revit. Execution tools will surface their own error if the
        # add-in isn't connected at call time.
        return True
