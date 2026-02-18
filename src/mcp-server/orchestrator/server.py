"""FastMCP entry point with dynamic tool registration."""

from __future__ import annotations

import copy
import json
import logging
from typing import Any

from mcp.server.fastmcp import FastMCP

from .config import Config
from .registry.registry import ToolRegistry
from .dispatcher.dispatcher import Dispatcher
from .dispatcher.result import ToolResult
from .adapters.revit_addin import RevitAddinAdapter
from .adapters.pyrevit import PyRevitAdapter
from .adapters.dynamo import DynamoAdapter
from .adapters.workflow import WorkflowAdapter
from .pipe.pipe_server import PipeServer
from .pipe.connection import PipeConnection
from .pipe.protocol import (
    make_tool_list_response,
    make_tool_add_response,
    make_tool_delete_response,
    make_tool_run_response,
    make_tool_load_response,
    make_tool_update_response,
    make_workflow_save_response,
    make_workflow_load_response,
    make_workflow_review_response,
    make_workflow_test_response,
    make_workflow_run_response,
    make_settings_response,
    make_settings_update_response,
    make_context_request,
    make_screenshot_request,
    make_connection_list_response,
    make_connection_add_response,
    make_connection_update_response,
    make_connection_delete_response,
    make_connection_test_response,
    make_connection_toggle_response,
)
from .connections.manager import ConnectionManager
from .connections.models import McpConnection
from .adapters.mcp_external import McpExternalAdapter
from .llm.router import LLMRouter
from .chat_session import ChatSession
from .execution_logger import ExecutionLogger
from .registry.file_parser import parse_file
from .telemetry.audit_log import AuditLog
from .store import SqliteEventStore, BlobStore
from .store.migration import JsonlMigrator
from .store.background_processor import BackgroundCardProcessor

import os
import signal
import threading

logger = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# Global instances — all ``None`` until ``init()`` is called.
# ---------------------------------------------------------------------------
config: Config | None = None
registry: ToolRegistry | None = None
mcp: FastMCP | None = None

_audit_log: AuditLog | None = None
_event_store: SqliteEventStore | None = None
_card_processor: BackgroundCardProcessor | None = None

revit_adapter: RevitAddinAdapter | None = None
pyrevit_adapter: PyRevitAdapter | None = None
dynamo_adapter: DynamoAdapter | None = None
workflow_adapter: WorkflowAdapter | None = None

adapters: dict[str, Any] = {}
dispatcher: Dispatcher | None = None
connection_manager: ConnectionManager | None = None
mcp_external_adapter: McpExternalAdapter | None = None

llm_router: LLMRouter | None = None
pipe_server: PipeServer | None = None
_sessions: dict[int, ChatSession] = {}

# Shutdown coordination
_shutdown_event = threading.Event()


async def _fetch_run_context(connection: PipeConnection) -> dict[str, Any]:
    """Request current Revit state from the C# add-in via the pipe."""
    try:
        msg = make_context_request()
        response = await connection.send_and_wait(msg, timeout=5.0)
        payload = response.get("payload", {})
        return {
            "doc_guid": payload.get("document_guid", ""),
            "doc_title": payload.get("document_title", ""),
            "user_name": payload.get("user_name", ""),
            "revit_version": payload.get("revit_version", ""),
            "active_view_type": payload.get("active_view_type", ""),
            "is_worksharing": payload.get("is_worksharing", False),
        }
    except Exception:
        logger.debug("Failed to fetch run context (non-fatal)")
        return {}


async def _capture_screenshot(
    connection: PipeConnection, event_id: str
) -> None:
    """Request a viewport screenshot from C# and store as an artifact."""
    store = _event_store
    if store is None or store.blob_store is None:
        return
    try:
        msg = make_screenshot_request()
        response = await connection.send_and_wait(msg, timeout=10.0)
        payload = response.get("payload", {})
        if not payload.get("success"):
            return

        image_b64 = payload.get("image_base64")
        if not image_b64:
            return

        import base64
        image_bytes = base64.b64decode(image_b64)
        sha, rel_path = store.blob_store.put(
            image_bytes, ext=".png", compress=False
        )
        store.add_artifact(
            event_id=event_id,
            kind="screenshot",
            sha256=sha,
            path=rel_path,
            size_bytes=len(image_bytes),
            mime_type=payload.get("mime_type", "image/png"),
        )
        logger.debug("Stored screenshot artifact for event %s", event_id)
    except Exception:
        logger.debug("Screenshot capture failed (non-fatal)", exc_info=True)


def _create_llm_provider(cfg: Config):
    """Create the LLM provider from config."""
    if cfg.llm_provider in ("openai", "openai-compatible"):
        from .llm.openai_provider import OpenAIProvider
        api_key = cfg.openai_api_key
        # Cloud OpenAI requires an API key; local models (openai-compatible) may not
        if cfg.llm_provider == "openai" and not api_key:
            raise RuntimeError(
                "OPENAI_API_KEY environment variable is not set. "
                "Set it and restart the server."
            )
        if cfg.llm_provider == "openai-compatible" and not cfg.openai_base_url:
            raise RuntimeError(
                "openai_base_url is required for openai-compatible provider."
            )
        # Use a dummy key for local models that don't need auth
        if not api_key:
            api_key = "not-needed"
        return OpenAIProvider(
            api_key=api_key,
            model=cfg.openai_model,
            base_url=cfg.openai_base_url or None,
        )
    else:
        if not cfg.anthropic_api_key:
            raise RuntimeError(
                "ANTHROPIC_API_KEY environment variable is not set. "
                "Set it and restart the server."
            )
        from .llm.claude_provider import ClaudeProvider
        return ClaudeProvider(api_key=cfg.anthropic_api_key, model=cfg.anthropic_model)


async def _on_pipe_connect(connection: PipeConnection) -> None:
    """Called when a C# client connects to the pipe."""
    revit_adapter.set_connection(connection)

    # Wire pipe connection to workflow adapter for reconciliation prompts
    if workflow_adapter is not None:
        workflow_adapter.set_pipe_connection(connection)

    # Always wire tool management callbacks
    connection._on_tool_list_request = _handle_tool_list
    connection._on_tool_add_request = _handle_tool_add
    connection._on_tool_delete_request = _handle_tool_delete
    connection._on_workflow_save_request = _handle_workflow_save
    connection._on_workflow_load_request = _handle_workflow_load
    connection._on_workflow_review_request = _handle_workflow_review
    connection._on_workflow_test_request = _handle_workflow_test
    connection._on_workflow_run_request = _handle_workflow_run
    connection._on_tool_run_request = _handle_tool_run
    connection._on_tool_load_request = _handle_tool_load
    connection._on_tool_update_request = _handle_tool_update
    connection._on_settings_request = _handle_settings
    connection._on_settings_update_request = _handle_settings_update
    connection._on_connection_list_request = _handle_connection_list
    connection._on_connection_add_request = _handle_connection_add
    connection._on_connection_update_request = _handle_connection_update
    connection._on_connection_delete_request = _handle_connection_delete
    connection._on_connection_test_request = _handle_connection_test
    connection._on_connection_toggle_request = _handle_connection_toggle

    if llm_router is not None:
        session = ChatSession(connection, llm_router, dispatcher, audit_log=_event_store or _audit_log)
        _sessions[id(connection)] = session
        connection.set_on_chat_message(_on_chat_message)
        logger.info("Pipe client connected, chat session created")
    else:
        logger.error(
            "Pipe client connected but LLM router is not initialized! "
            "Chat messages will be ignored. Check your API key configuration."
        )


async def _on_pipe_disconnect(connection: PipeConnection) -> None:
    """Called when a C# client disconnects."""
    _sessions.pop(id(connection), None)
    # Clear the adapter connection if it was this one
    if revit_adapter._connection is connection:
        revit_adapter.set_connection(None)
    # Clear workflow adapter pipe connection
    if workflow_adapter is not None and workflow_adapter._pipe_connection is connection:
        workflow_adapter.set_pipe_connection(None)
    logger.info("Pipe client disconnected, chat session removed")


async def _on_chat_message(connection: PipeConnection, message: dict[str, Any]) -> None:
    """Route an incoming chat_message to the appropriate ChatSession."""
    logger.debug("Received chat_message, looking up session for connection %s", id(connection))
    session = _sessions.get(id(connection))
    if session is None:
        logger.error("chat_message from unknown connection (id=%s), active sessions: %s",
                      id(connection), list(_sessions.keys()))
        return
    logger.debug("Routing chat message to session")
    await session.handle_user_message(message)


async def _handle_tool_list(connection: PipeConnection, message: dict[str, Any]) -> None:
    """Handle a tool_list_request — return all registered tools."""
    logger.debug("Handling tool_list_request")
    tools = registry.list_tools()
    # Build a compact summary for each tool
    tool_summaries = []
    for t in tools:
        summary: dict[str, Any] = {
            "name": t.get("name", ""),
            "description": t.get("description", ""),
            "adapter": t.get("adapter", ""),
            "parameter_count": len(
                t.get("parameters", {}).get("properties", {})
            ),
            "parameters": t.get("parameters", {}),
        }
        # Include execution modes if present
        if "execution" in t:
            summary["execution"] = t["execution"]
        # Include visibility
        summary["visibility"] = t.get("visibility", "user")
        # Include governance/metadata fields
        if "side_effects" in t:
            summary["side_effects"] = t["side_effects"]
        if "permissions" in t:
            summary["permissions"] = t["permissions"]
        if "version" in t:
            summary["version"] = t["version"]
        if "author" in t:
            summary["author"] = t["author"]
        if "tags" in t:
            summary["tags"] = t["tags"]
        tool_summaries.append(summary)
    response = make_tool_list_response(tool_summaries)
    await connection.send(response)


def _infer_json_schema_type(value: Any) -> str:
    """Infer a JSON Schema type string from a Python value."""
    if isinstance(value, bool):
        return "boolean"
    if isinstance(value, int):
        return "integer"
    if isinstance(value, float):
        return "number"
    if isinstance(value, list):
        return "array"
    if isinstance(value, dict):
        return "object"
    return "string"


def _auto_derive_workflow_contract(
    workflow_steps: list[dict[str, Any]],
    reg: ToolRegistry,
) -> dict[str, Any]:
    """Derive governance fields from the constituent step tools.

    Returns a dict with ``side_effects``, ``permission_mode``, and
    ``preconditions`` inferred by taking the union across all step tools.
    """
    all_side_effects: set[str] = set()
    has_write = False
    seen_preconditions: dict[str, dict[str, Any]] = {}  # keyed by "check"

    for step in workflow_steps:
        tool_name = step.get("tool", "")
        if not tool_name:
            continue
        tool_def = reg.get(tool_name)
        if tool_def is None:
            logger.warning(
                "auto-derive: step tool '%s' not found in registry, skipping",
                tool_name,
            )
            continue

        for se in tool_def.get("side_effects", []):
            all_side_effects.add(se)

        perms = tool_def.get("permissions", {})
        if perms.get("mode") == "write":
            has_write = True

        for pc in tool_def.get("preconditions", []):
            check = pc.get("check", "")
            if check and check not in seen_preconditions:
                seen_preconditions[check] = pc

    return {
        "side_effects": sorted(all_side_effects),
        "permission_mode": "write" if has_write else "read",
        "preconditions": list(seen_preconditions.values()),
    }


async def _handle_workflow_save(connection: PipeConnection, message: dict[str, Any]) -> None:
    """Handle a workflow_save_request — save execution history as a workflow tool."""
    logger.debug("Handling workflow_save_request")
    payload = message.get("payload", {})
    name = payload.get("name", "")
    description = payload.get("description", "")
    steps = payload.get("steps", [])

    try:
        if not name:
            await connection.send(
                make_workflow_save_response(False, error="Workflow name is required")
            )
            return

        if not steps:
            await connection.send(
                make_workflow_save_response(False, error="No steps provided")
            )
            return

        # Build the workflow definition with step IDs
        workflow_steps = []
        for i, step in enumerate(steps):
            workflow_step: dict[str, Any] = {
                "id": f"step{i + 1}",
                "tool": step.get("tool_name", ""),
                "args": step.get("args", {}),
            }
            # Support optional parameter bindings from the save request
            if step.get("bindings"):
                workflow_step["bindings"] = step["bindings"]
            if step.get("guard"):
                workflow_step["guard"] = step["guard"]
            if step.get("on_failure") and step["on_failure"] != "stop":
                workflow_step["on_failure"] = step["on_failure"]
            if step.get("max_retries"):
                workflow_step["max_retries"] = step["max_retries"]
            if step.get("timeout_ms"):
                workflow_step["timeout_ms"] = step["timeout_ms"]
            workflow_steps.append(workflow_step)

        # Embed tool snapshots (copy semantics)
        for i, ws in enumerate(workflow_steps):
            # Check if C# sent an existing snapshot (re-save preserves isolation)
            orig_step = steps[i] if i < len(steps) else {}
            existing_snapshot = orig_step.get("snapshot")

            if existing_snapshot:
                ws["snapshot"] = existing_snapshot
            else:
                # Capture fresh snapshot from registry
                tool_name = ws.get("tool", "")
                if tool_name:
                    tool_def = registry.get(tool_name)
                    if tool_def is not None:
                        snap = copy.deepcopy(tool_def)
                        # Strip workflow-internal and governance fields
                        snap.pop("workflow", None)
                        snap.pop("governance", None)
                        snap.pop("metadata", None)
                        snap.pop("visibility", None)
                        # Tag source type
                        snap["source_type"] = "recording" if tool_name.startswith("recorded.") else "tool"
                        ws["snapshot"] = snap

        # Extract governance fields from payload
        author = payload.get("author", "")
        tags = payload.get("tags", [])
        version = payload.get("version", "1.0.0")
        permission_mode = payload.get("permission_mode", "write")
        approval_required = payload.get("approval_required", False)
        side_effects = payload.get("side_effects", [])
        llm_review = payload.get("llm_review")

        # Auto-derive governance from step tools as fallback
        derived = _auto_derive_workflow_contract(workflow_steps, registry)
        if not side_effects:
            side_effects = derived["side_effects"]
        if not permission_mode or permission_mode == "write":
            permission_mode = derived.get("permission_mode", "write")

        # Build the definitive tool name (must contain a dot for schema validation)
        # Ensure it has a namespace prefix
        import re
        if not re.match(r"^[a-z][a-z0-9]*\.", name):
            # Name lacks a valid namespace prefix — add "captured."
            safe_name = f"captured.{name}"
        else:
            safe_name = name
        # Sanitize: replace any non-alphanumeric/dot/underscore chars
        safe_name = re.sub(r"[^a-z0-9._]", "_", safe_name.lower())
        # Ensure no consecutive dots and no trailing dots
        safe_name = re.sub(r"\.{2,}", ".", safe_name).strip(".")

        # Build promoted parameters from the payload
        promoted = payload.get("promoted_parameters", {})
        param_properties: dict[str, Any] = {}
        for key, info in promoted.items():
            prop: dict[str, Any] = {
                "type": info.get("type") or _infer_json_schema_type(info.get("default")),
                "description": info.get("description", f"Workflow parameter: {key}"),
            }
            if info.get("default") is not None:
                prop["default"] = info["default"]
            param_properties[key] = prop

        # Inject $input bindings into steps that use promoted keys
        for ws in workflow_steps:
            step_args = ws.get("args", {})
            for key in promoted:
                if key in step_args:
                    ws.setdefault("bindings", {})[key] = f"$input.{key}"

        workflow_definition: dict[str, Any] = {
            "name": safe_name,
            "description": description or f"Captured workflow: {safe_name}",
            "adapter": "workflow",
            "parameters": {
                "type": "object",
                "properties": param_properties,
                "required": [],
            },
            "workflow": {
                "steps": workflow_steps,
            },
        }

        # Add governance metadata
        governance: dict[str, Any] = {}
        if author:
            governance["author"] = author
        if tags:
            governance["tags"] = tags
        governance["version"] = version
        if permission_mode:
            governance["permission_mode"] = permission_mode
        if approval_required:
            governance["approval_required"] = approval_required
        if side_effects:
            governance["side_effects"] = side_effects
        if governance:
            workflow_definition["governance"] = governance

        # Include LLM review metadata if provided
        if llm_review:
            workflow_definition.setdefault("metadata", {})["llm_review"] = llm_review

        # Write to tools directory
        tools_dir = config.tools_dir
        tools_dir.mkdir(parents=True, exist_ok=True)

        json_filename = f"{safe_name}.json"
        tool_json_path = tools_dir / json_filename

        with open(tool_json_path, "w", encoding="utf-8") as f:
            json.dump(workflow_definition, f, indent=2)

        logger.info("Saved workflow to %s", tool_json_path)

        # Handle rename: delete old file and unregister old name immediately.
        # Always unregister from registry even when hot-reload is enabled,
        # because the watchdog event fires asynchronously and the UI may
        # refresh before it triggers, causing a duplicate entry.
        original_name = payload.get("original_name", "")
        if original_name and original_name != safe_name:
            old_filename = f"{original_name}.json"
            old_path = tools_dir / old_filename
            if old_path.exists():
                old_path.unlink()
                logger.info("Deleted old workflow file on rename: %s", old_path)
            registry.unregister(original_name)

        # Register the new definition immediately (watchdog may lag behind)
        registry.register(workflow_definition)
        if not config.watch_tools_dir:
            _register_single_tool(workflow_definition)

        # Audit log (write to event store if available, otherwise audit log)
        _log_target = _event_store or _audit_log
        if _log_target is not None:
            _log_target.log_event({
                "event_type": "workflow_saved",
                "workflow_name": safe_name,
                "author": author,
                "version": version,
                "tags": tags,
                "permission_mode": permission_mode,
                "approval_required": approval_required,
                "side_effects": side_effects,
                "step_count": len(workflow_steps),
                "was_llm_reviewed": llm_review is not None,
                "llm_review_summary": llm_review.get("summary", "") if llm_review else "",
            })

        await connection.send(
            make_workflow_save_response(True, workflow_name=safe_name)
        )

    except Exception as e:
        logger.exception("Error handling workflow_save_request")
        await connection.send(
            make_workflow_save_response(False, error=str(e))
        )


async def _handle_tool_add(connection: PipeConnection, message: dict[str, Any]) -> None:
    """Handle a tool_add_request — parse the file and register a new tool."""
    logger.debug("Handling tool_add_request")
    payload = message.get("payload", {})
    file_path = payload.get("file_path", "")
    metadata = payload.get("metadata")

    try:
        from pathlib import Path as _Path

        path = _Path(file_path)
        if not path.exists():
            await connection.send(
                make_tool_add_response(False, error=f"File not found: {file_path}")
            )
            return

        # Auto-parse the file
        definition = parse_file(path)

        # Remove internal fields
        source_path = definition.pop("_source_path", None)

        # Merge any user-provided metadata overrides
        if metadata:
            if metadata.get("name"):
                definition["name"] = metadata["name"]
            if metadata.get("description"):
                definition["description"] = metadata["description"]

        # Write the JSON definition to the tools directory
        tool_name = definition["name"]
        json_filename = f"{tool_name}.json"
        tools_dir = config.tools_dir
        tools_dir.mkdir(parents=True, exist_ok=True)
        tool_json_path = tools_dir / json_filename

        with open(tool_json_path, "w", encoding="utf-8") as f:
            json.dump(definition, f, indent=2)

        logger.info("Wrote tool definition to %s", tool_json_path)

        # The watchdog hot-reload will pick it up automatically.
        # If watching is disabled, register manually.
        if not config.watch_tools_dir:
            registry.register(definition)
            _register_single_tool(definition)

        await connection.send(
            make_tool_add_response(
                True,
                tool_name=tool_name,
                tool={
                    "name": definition.get("name", ""),
                    "description": definition.get("description", ""),
                    "adapter": definition.get("adapter", ""),
                    "parameter_count": len(
                        definition.get("parameters", {}).get("properties", {})
                    ),
                },
            )
        )
    except Exception as e:
        logger.exception("Error handling tool_add_request")
        await connection.send(
            make_tool_add_response(False, error=str(e))
        )


async def _handle_tool_delete(connection: PipeConnection, message: dict[str, Any]) -> None:
    """Handle a tool_delete_request — delete a tool's JSON file and unregister it."""
    logger.debug("Handling tool_delete_request")
    payload = message.get("payload", {})
    tool_name = payload.get("tool_name", "")

    try:
        if not tool_name:
            await connection.send(
                make_tool_delete_response(False, error="Tool name is required")
            )
            return

        # Validate the tool exists
        tool_def = registry.get(tool_name)
        if tool_def is None:
            await connection.send(
                make_tool_delete_response(False, error=f"Tool '{tool_name}' not found")
            )
            return

        # Delete the JSON file
        tool_json_path = config.tools_dir / f"{tool_name}.json"
        if tool_json_path.exists():
            tool_json_path.unlink()
            logger.info("Deleted tool file: %s", tool_json_path)

        # Unregister from the registry (always do it immediately so the UI
        # reflects the change even if the watchdog hasn't fired yet)
        registry.unregister(tool_name)

        # Audit log
        _log_target = _event_store or _audit_log
        if _log_target is not None:
            _log_target.log_event({
                "event_type": "tool_deleted",
                "tool_name": tool_name,
            })

        await connection.send(
            make_tool_delete_response(True, tool_name=tool_name)
        )

    except Exception as e:
        logger.exception("Error handling tool_delete_request")
        await connection.send(
            make_tool_delete_response(False, error=str(e))
        )


async def _handle_tool_run(connection: PipeConnection, message: dict[str, Any]) -> None:
    """Handle a tool_run_request — execute any tool with input arguments."""
    logger.debug("Handling tool_run_request")
    payload = message.get("payload", {})
    tool_name = payload.get("tool_name", "")
    input_args = payload.get("input_args") or {}  # coerce null/None to empty dict
    execution_mode = payload.get("execution_mode", "headless")

    try:
        if not tool_name:
            await connection.send(
                make_tool_run_response(False, error="Tool name is required")
            )
            return

        tool_def = registry.get(tool_name)
        if tool_def is None:
            await connection.send(
                make_tool_run_response(False, error=f"Tool '{tool_name}' not found")
            )
            return

        # Fetch run context from Revit
        context = await _fetch_run_context(connection)

        # Log execution events so manual runs appear in the History tab
        exec_logger = ExecutionLogger(connection, audit_log=_event_store or _audit_log, origin="manual")
        exec_logger.set_run_context(context)
        correlation_id = exec_logger.start_correlation()
        event_id = await exec_logger.log_started(tool_name, input_args)

        outcome = "completed"
        try:
            result = await dispatcher.dispatch(
                tool_name, input_args, execution_mode=execution_mode
            )
            if result.success:
                await exec_logger.log_completed(event_id, result, tool_name, input_args)
                await _capture_screenshot(connection, event_id)
            else:
                outcome = "failed"
                await exec_logger.log_failed(
                    event_id,
                    result.error_message or "Unknown error",
                    tool_name,
                    input_args,
                )
        except Exception as dispatch_err:
            outcome = "failed"
            await exec_logger.log_failed(event_id, str(dispatch_err), tool_name, input_args)
            raise
        finally:
            exec_logger.log_correlation_summary(correlation_id, outcome=outcome)

        await connection.send(
            make_tool_run_response(
                success=result.success,
                data=result.data,
                error=result.error_message if not result.success else "",
                error_code=result.error_code if not result.success else "",
                stage=result.stage,
            )
        )

    except Exception as e:
        logger.exception("Error handling tool_run_request")
        await connection.send(
            make_tool_run_response(False, error=str(e), stage="dispatch")
        )


async def _handle_tool_load(connection: PipeConnection, message: dict[str, Any]) -> None:
    """Handle a tool_load_request — return the full tool definition for editing."""
    logger.debug("Handling tool_load_request")
    payload = message.get("payload", {})
    tool_name = payload.get("tool_name", "")

    try:
        if not tool_name:
            await connection.send(
                make_tool_load_response(False, error="Tool name is required")
            )
            return

        tool_def = registry.get(tool_name)
        if tool_def is None:
            await connection.send(
                make_tool_load_response(False, error=f"Tool '{tool_name}' not found")
            )
            return

        await connection.send(
            make_tool_load_response(True, tool=tool_def)
        )

    except Exception as e:
        logger.exception("Error handling tool_load_request")
        await connection.send(
            make_tool_load_response(False, error=str(e))
        )


async def _handle_tool_update(connection: PipeConnection, message: dict[str, Any]) -> None:
    """Handle a tool_update_request — validate, write, and re-register a tool definition."""
    logger.debug("Handling tool_update_request")
    payload = message.get("payload", {})
    tool_name = payload.get("tool_name", "")
    definition = payload.get("definition", {})

    try:
        if not tool_name:
            await connection.send(
                make_tool_update_response(False, error="Tool name is required")
            )
            return

        if not definition:
            await connection.send(
                make_tool_update_response(False, error="Definition is required")
            )
            return

        # Validate the definition against the schema
        from .registry.schema_validator import validate_tool_definition

        errors = validate_tool_definition(definition)
        if errors:
            await connection.send(
                make_tool_update_response(
                    False, error=f"Validation failed: {'; '.join(errors)}"
                )
            )
            return

        # Write the updated definition to disk
        tools_dir = config.tools_dir
        tools_dir.mkdir(parents=True, exist_ok=True)
        tool_json_path = tools_dir / f"{tool_name}.json"

        with open(tool_json_path, "w", encoding="utf-8") as f:
            json.dump(definition, f, indent=2)

        logger.info("Updated tool definition: %s", tool_json_path)

        # Re-register in the registry
        registry.register(definition)

        # Audit log
        _log_target = _event_store or _audit_log
        if _log_target is not None:
            _log_target.log_event({
                "event_type": "tool_updated",
                "tool_name": tool_name,
            })

        await connection.send(
            make_tool_update_response(True, tool_name=tool_name)
        )

    except Exception as e:
        logger.exception("Error handling tool_update_request")
        await connection.send(
            make_tool_update_response(False, error=str(e))
        )


async def _handle_workflow_load(connection: PipeConnection, message: dict[str, Any]) -> None:
    """Handle a workflow_load_request — return full workflow definition for editing."""
    logger.debug("Handling workflow_load_request")
    payload = message.get("payload", {})
    workflow_name = payload.get("workflow_name", "")

    try:
        if not workflow_name:
            await connection.send(
                make_workflow_load_response(False, error="Workflow name is required")
            )
            return

        # Look up the tool definition in the registry
        tool_def = registry.get(workflow_name)
        if tool_def is None:
            await connection.send(
                make_workflow_load_response(False, error=f"Workflow '{workflow_name}' not found")
            )
            return

        if tool_def.get("adapter") != "workflow":
            await connection.send(
                make_workflow_load_response(False, error=f"'{workflow_name}' is not a workflow")
            )
            return

        # Build the response with full workflow data including governance
        workflow_data: dict[str, Any] = {
            "name": tool_def.get("name", ""),
            "description": tool_def.get("description", ""),
            "steps": tool_def.get("workflow", {}).get("steps", []),
            "governance": tool_def.get("governance", {}),
            "metadata": tool_def.get("metadata", {}),
            "parameters": tool_def.get("parameters", {}),
        }

        # Auto-derive contract from step tools
        derived = _auto_derive_workflow_contract(
            tool_def.get("workflow", {}).get("steps", []),
            registry,
        )
        workflow_data["derived_contract"] = derived

        await connection.send(
            make_workflow_load_response(True, workflow=workflow_data)
        )

    except Exception as e:
        logger.exception("Error handling workflow_load_request")
        await connection.send(
            make_workflow_load_response(False, error=str(e))
        )


async def _handle_workflow_review(connection: PipeConnection, message: dict[str, Any]) -> None:
    """Handle a workflow_review_request — LLM-review a workflow before saving."""
    logger.debug("Handling workflow_review_request")
    payload = message.get("payload", {})
    steps = payload.get("steps", [])
    description = payload.get("description", "")

    try:
        if llm_router is None or llm_router._provider is None:
            await connection.send(
                make_workflow_review_response(False, error="LLM provider not available")
            )
            return

        from .llm.workflow_reviewer import WorkflowReviewer

        reviewer = WorkflowReviewer(llm_router._provider)
        result = await reviewer.review(steps, description)

        await connection.send(
            make_workflow_review_response(
                success=True,
                summary=result.get("summary", ""),
                flags=result.get("flags", []),
                suggestions=result.get("suggestions", []),
            )
        )

    except Exception as e:
        logger.exception("Error handling workflow_review_request")
        await connection.send(
            make_workflow_review_response(False, error=str(e))
        )


async def _handle_workflow_test(connection: PipeConnection, message: dict[str, Any]) -> None:
    """Handle a workflow_test_request — execute the workflow for real in Revit.

    The C# side closes the modal editor dialog before sending this request,
    allowing Revit's ExternalEvent to process tool calls normally.
    """
    logger.debug("Handling workflow_test_request")
    payload = message.get("payload", {})
    steps = payload.get("steps", [])
    description = payload.get("description", "")

    try:
        if not steps:
            await connection.send(
                make_workflow_test_response(False, error="No steps to test")
            )
            return

        # Build a temporary workflow definition for the engine
        temp_workflow_def: dict[str, Any] = {
            "steps": steps,
        }

        from .workflow.engine import WorkflowEngine

        # Fetch run context from Revit
        context = await _fetch_run_context(connection)

        exec_logger = ExecutionLogger(connection, audit_log=_event_store or _audit_log, origin="test")
        exec_logger.set_run_context(context)
        correlation_id = exec_logger.start_correlation()
        engine = WorkflowEngine(dispatcher, audit_log=_audit_log, execution_logger=exec_logger)
        result = await engine.execute(temp_workflow_def)

        # Finalize the episode
        exec_logger.log_correlation_summary(
            correlation_id,
            outcome="completed" if result.success else "failed",
        )

        # Build step summaries for the response
        step_summaries: dict[str, Any] = {}
        for step_id, r in result.step_results.items():
            step_summaries[step_id] = {
                "success": r.success,
                "error_code": r.error_code,
                "error_message": r.error_message,
                "duration_ms": r.duration_ms,
            }

        await connection.send(
            make_workflow_test_response(
                success=result.success,
                steps_completed=result.steps_completed,
                steps_total=result.steps_total,
                step_summaries=step_summaries,
                skipped_steps=result.skipped_steps,
                total_duration_ms=result.total_duration_ms,
            )
        )

    except Exception as e:
        logger.exception("Error handling workflow_test_request")
        await connection.send(
            make_workflow_test_response(False, error=str(e))
        )


async def _handle_workflow_run(connection: PipeConnection, message: dict[str, Any]) -> None:
    """Handle a workflow_run_request — execute a saved workflow with input arguments."""
    logger.debug("Handling workflow_run_request")
    payload = message.get("payload", {})
    workflow_name = payload.get("workflow_name", "")
    input_args = payload.get("input_args", {})

    try:
        if not workflow_name:
            await connection.send(
                make_workflow_run_response(False, error="Workflow name is required")
            )
            return

        tool_def = registry.get(workflow_name)
        if tool_def is None:
            await connection.send(
                make_workflow_run_response(False, error=f"Workflow '{workflow_name}' not found")
            )
            return

        if tool_def.get("adapter") != "workflow":
            await connection.send(
                make_workflow_run_response(False, error=f"'{workflow_name}' is not a workflow")
            )
            return

        # Fetch run context from Revit
        context = await _fetch_run_context(connection)

        # Log execution events so manual workflow runs appear in the History tab
        exec_logger = ExecutionLogger(connection, audit_log=_event_store or _audit_log, origin="manual")
        exec_logger.set_run_context(context)
        correlation_id = exec_logger.start_correlation()
        event_id = await exec_logger.log_started(workflow_name, input_args)

        outcome = "completed"
        try:
            result = await dispatcher.dispatch(workflow_name, input_args)
            if result.success:
                await exec_logger.log_completed(event_id, result, workflow_name, input_args)
                await _capture_screenshot(connection, event_id)
            else:
                outcome = "failed"
                await exec_logger.log_failed(
                    event_id,
                    result.error_message or "Unknown error",
                    workflow_name,
                    input_args,
                )
        except Exception as dispatch_err:
            outcome = "failed"
            await exec_logger.log_failed(event_id, str(dispatch_err), workflow_name, input_args)
            raise
        finally:
            exec_logger.log_correlation_summary(correlation_id, outcome=outcome)

        await connection.send(
            make_workflow_run_response(
                success=result.success,
                data=result.data,
                error=result.error_message if not result.success else "",
            )
        )

    except Exception as e:
        logger.exception("Error handling workflow_run_request")
        await connection.send(
            make_workflow_run_response(False, error=str(e))
        )


async def _handle_settings(connection: PipeConnection, message: dict[str, Any]) -> None:
    """Handle a settings_request — return server configuration info."""
    logger.debug("Handling settings_request")
    try:
        # Resolve paths to absolute for display
        db_path = str(config.event_store_path.resolve()) if config.event_store_path else ""
        blob_path = str(config.blob_store_dir.resolve()) if config.blob_store_dir else ""
        log_path = str(config.audit_log_dir.resolve()) if config.audit_log_dir else ""
        tools_path = str(config.tools_dir.resolve()) if config.tools_dir else ""

        # Compute DB size
        db_size_bytes = 0
        if config.event_store_path.exists():
            db_size_bytes = config.event_store_path.stat().st_size

        # Count blobs
        blob_count = 0
        if config.blob_store_dir.exists():
            blob_count = sum(1 for _ in config.blob_store_dir.rglob("*") if _.is_file())

        # Check ML availability
        ml_available = False
        try:
            from .store.embeddings import EmbeddingEngine
            ml_available = EmbeddingEngine.available()
        except Exception:
            pass

        settings = {
            "pipe_name": config.pipe_name,
            "tools_dir": tools_path,
            "tool_count": len(registry.list_tool_names()),
            "llm_provider": config.llm_provider,
            "llm_model": config.anthropic_model if config.llm_provider == "claude" else config.openai_model,
            "openai_base_url": config.openai_base_url,
            "event_store_path": db_path,
            "event_store_size_bytes": db_size_bytes,
            "blob_store_dir": blob_path,
            "blob_count": blob_count,
            "audit_log_dir": log_path,
            "embedding_model": config.embedding_model,
            "ml_available": ml_available,
            "migrate_jsonl_on_startup": config.migrate_jsonl_on_startup,
            "watch_tools_dir": config.watch_tools_dir,
        }

        # Episode/event counts from the store
        if _event_store is not None:
            try:
                row = _event_store._conn.execute(
                    "SELECT COUNT(*) FROM episodes"
                ).fetchone()
                settings["episode_count"] = row[0] if row else 0
                row = _event_store._conn.execute(
                    "SELECT COUNT(*) FROM events"
                ).fetchone()
                settings["event_count"] = row[0] if row else 0
            except Exception:
                settings["episode_count"] = 0
                settings["event_count"] = 0

        await connection.send(make_settings_response(settings))
    except Exception as e:
        logger.exception("Error handling settings_request")
        await connection.send(make_settings_response({"error": str(e)}))


async def _handle_settings_update(connection: PipeConnection, message: dict[str, Any]) -> None:
    """Handle a settings_update_request — update LLM config and hot-swap the provider."""
    global llm_router
    logger.debug("Handling settings_update_request")
    payload = message.get("payload", {})

    try:
        llm_provider = payload.get("llm_provider", "")
        api_key = payload.get("api_key", "")
        model = payload.get("model", "")
        base_url = payload.get("base_url", "")

        # Validate required fields
        if not llm_provider:
            await connection.send(make_settings_update_response(False, error="Provider is required"))
            return
        if not model:
            await connection.send(make_settings_update_response(False, error="Model is required"))
            return
        if llm_provider in ("claude", "openai") and not api_key:
            await connection.send(make_settings_update_response(False, error="API key is required for cloud providers"))
            return
        if llm_provider == "openai-compatible" and not base_url:
            await connection.send(make_settings_update_response(False, error="Base URL is required for OpenAI-compatible providers"))
            return

        # Map generic api_key to provider-specific field
        updates: dict[str, str] = {"llm_provider": llm_provider}
        if llm_provider == "claude":
            updates["anthropic_api_key"] = api_key
            updates["anthropic_model"] = model
        else:
            updates["openai_api_key"] = api_key
            updates["openai_model"] = model
            updates["openai_base_url"] = base_url

        # Persist to config
        config.apply_update(updates)

        # Create new LLM provider and swap the router
        new_provider = _create_llm_provider(config)
        llm_router = LLMRouter(new_provider, registry)
        logger.info("LLM provider hot-swapped to %s (%s)", llm_provider, model)

        # Update all active ChatSession objects to use the new router
        for session in _sessions.values():
            session._router = llm_router

        # If this connection has no ChatSession yet (e.g. server started without
        # an API key and the user just configured one), create one now.
        conn_id = id(connection)
        if conn_id not in _sessions:
            session = ChatSession(connection, llm_router, dispatcher, audit_log=_event_store or _audit_log)
            _sessions[conn_id] = session
            connection.set_on_chat_message(_on_chat_message)
            logger.info("ChatSession created for existing connection after settings update")

        await connection.send(make_settings_update_response(True))

    except Exception as e:
        logger.exception("Error handling settings_update_request")
        await connection.send(make_settings_update_response(False, error=str(e)))


async def _handle_connection_list(connection: PipeConnection, message: dict[str, Any]) -> None:
    """Handle a connection_list_request — return all connections (sans credentials)."""
    logger.debug("Handling connection_list_request")
    try:
        connections = connection_manager.get_all_connections()
        summaries = [c.to_summary_dict() for c in connections]
        await connection.send(make_connection_list_response(summaries))
    except Exception as e:
        logger.exception("Error handling connection_list_request")
        await connection.send(make_connection_list_response([]))


async def _handle_connection_add(connection: PipeConnection, message: dict[str, Any]) -> None:
    """Handle a connection_add_request — validate, save, auto-connect if enabled."""
    logger.debug("Handling connection_add_request")
    payload = message.get("payload", {})
    try:
        conn = McpConnection.from_dict(payload)
        conn = connection_manager.add_connection(conn)

        # Auto-connect if enabled
        if conn.enabled:
            try:
                await connection_manager.connect(conn.id)
            except Exception:
                logger.warning("Auto-connect failed for new connection '%s'", conn.name)

        await connection.send(
            make_connection_add_response(True, connection=conn.to_summary_dict())
        )
    except Exception as e:
        logger.exception("Error handling connection_add_request")
        await connection.send(
            make_connection_add_response(False, error=str(e))
        )


async def _handle_connection_update(connection: PipeConnection, message: dict[str, Any]) -> None:
    """Handle a connection_update_request — update fields, reconnect if needed."""
    logger.debug("Handling connection_update_request")
    payload = message.get("payload", {})
    conn_id = payload.pop("connection_id", "")
    try:
        if not conn_id:
            await connection.send(
                make_connection_update_response(False, error="connection_id is required")
            )
            return

        # Check if endpoint-related fields changed (needs reconnect)
        endpoint_fields = {"command", "args", "url", "auth_credential_enc", "transport"}
        needs_reconnect = any(k in payload for k in endpoint_fields)

        conn = connection_manager.update_connection(conn_id, payload)
        if conn is None:
            await connection.send(
                make_connection_update_response(False, error="Connection not found")
            )
            return

        # Reconnect if endpoint changed and connection was active
        if needs_reconnect and conn.status == "connected":
            try:
                await connection_manager.disconnect(conn_id)
                await connection_manager.connect(conn_id)
            except Exception:
                logger.warning("Reconnect failed after update for '%s'", conn.name)

        await connection.send(
            make_connection_update_response(True, connection=conn.to_summary_dict())
        )
    except Exception as e:
        logger.exception("Error handling connection_update_request")
        await connection.send(
            make_connection_update_response(False, error=str(e))
        )


async def _handle_connection_delete(connection: PipeConnection, message: dict[str, Any]) -> None:
    """Handle a connection_delete_request — disconnect + remove."""
    logger.debug("Handling connection_delete_request")
    payload = message.get("payload", {})
    conn_id = payload.get("connection_id", "")
    try:
        if not conn_id:
            await connection.send(
                make_connection_delete_response(False, error="connection_id is required")
            )
            return

        # Disconnect first
        try:
            await connection_manager.disconnect(conn_id)
        except Exception:
            pass

        removed = connection_manager.remove_connection(conn_id)
        if not removed:
            await connection.send(
                make_connection_delete_response(False, error="Connection not found")
            )
            return

        await connection.send(make_connection_delete_response(True))
    except Exception as e:
        logger.exception("Error handling connection_delete_request")
        await connection.send(
            make_connection_delete_response(False, error=str(e))
        )


async def _handle_connection_test(connection: PipeConnection, message: dict[str, Any]) -> None:
    """Handle a connection_test_request — temporary connect, discover, disconnect."""
    logger.debug("Handling connection_test_request")
    payload = message.get("payload", {})
    conn_id = payload.get("connection_id", "")
    try:
        if not conn_id:
            await connection.send(
                make_connection_test_response(False, error="connection_id is required")
            )
            return

        result = await connection_manager.test_connection(conn_id)
        if result["success"]:
            await connection.send(
                make_connection_test_response(
                    True,
                    tools=result.get("discovered_tools", []),
                    server_name=result.get("server_name", ""),
                    server_version=result.get("server_version", ""),
                )
            )
        else:
            await connection.send(
                make_connection_test_response(False, error=result.get("error", ""))
            )
    except Exception as e:
        logger.exception("Error handling connection_test_request")
        await connection.send(
            make_connection_test_response(False, error=str(e))
        )


async def _handle_connection_toggle(connection: PipeConnection, message: dict[str, Any]) -> None:
    """Handle a connection_toggle_request — enable/disable with connect/disconnect."""
    logger.debug("Handling connection_toggle_request")
    payload = message.get("payload", {})
    conn_id = payload.get("connection_id", "")
    enabled = payload.get("enabled", True)
    try:
        if not conn_id:
            await connection.send(
                make_connection_toggle_response(False, error="connection_id is required")
            )
            return

        if enabled:
            conn = await connection_manager.enable_connection(conn_id)
        else:
            conn = await connection_manager.disable_connection(conn_id)

        if conn is None:
            await connection.send(
                make_connection_toggle_response(False, error="Connection not found")
            )
            return

        await connection.send(
            make_connection_toggle_response(True, connection=conn.to_summary_dict())
        )
    except Exception as e:
        logger.exception("Error handling connection_toggle_request")
        await connection.send(
            make_connection_toggle_response(False, error=str(e))
        )


def _register_mcp_tools() -> None:
    """Register all tools from the registry as MCP tools.

    Tools with visibility 'internal' are skipped — they should not be
    callable by the LLM.  'llm-only' and 'user' tools are both registered.
    """
    for definition in registry.list_tools():
        if definition.get("visibility", "user") == "internal":
            continue
        _register_single_tool(definition)


def _make_tool_handler(name: str):
    """Create a tool handler function bound to a specific tool name."""
    async def tool_handler(arguments: dict[str, Any]) -> dict[str, Any]:
        result = await dispatcher.dispatch(name, arguments)
        return result.to_dict()
    tool_handler.__name__ = name.replace(".", "_")
    return tool_handler


def _register_single_tool(definition: dict[str, Any]) -> None:
    """Register a single tool definition as an MCP tool."""
    tool_name = definition["name"]
    description = definition["description"]

    handler = _make_tool_handler(tool_name)

    mcp.tool(
        name=tool_name,
        description=description,
    )(handler)

    logger.info("Registered MCP tool: %s", tool_name)


def init() -> None:
    """Initialize the server: load tools, start pipe server, create LLM router."""
    global config, registry, mcp, llm_router, pipe_server
    global _audit_log, _event_store, _card_processor
    global revit_adapter, pyrevit_adapter, dynamo_adapter, workflow_adapter
    global adapters, dispatcher, connection_manager, mcp_external_adapter

    config = Config.from_env()

    # Create core objects
    registry = ToolRegistry()
    mcp = FastMCP("Revit Orchestrator")

    revit_adapter = RevitAddinAdapter()
    pyrevit_adapter = PyRevitAdapter()
    dynamo_adapter = DynamoAdapter()
    workflow_adapter = WorkflowAdapter(registry=registry)

    adapters = {
        "revit": revit_adapter,
        "pyrevit": pyrevit_adapter,
        "dynamo": dynamo_adapter,
        "workflow": workflow_adapter,
    }

    dispatcher = Dispatcher(registry, adapters, config.handlers_dir)

    dynamo_adapter.set_revit_adapter(revit_adapter)
    pyrevit_adapter.set_revit_adapter(revit_adapter)
    workflow_adapter.set_dispatcher(dispatcher)

    # Initialize audit log (kept for backward compat)
    _audit_log = AuditLog(config.audit_log_dir)
    logger.info("Audit log initialized at %s", config.audit_log_dir)

    # Initialize SQLite event store + blob store
    blob_store = BlobStore(config.blob_store_dir)
    _event_store = SqliteEventStore(config.event_store_path, blob_store=blob_store)
    logger.info("Event store initialized at %s", config.event_store_path)

    # Migrate existing JSONL logs on startup
    if config.migrate_jsonl_on_startup:
        try:
            migrator = JsonlMigrator(_event_store, config.audit_log_dir)
            imported = migrator.migrate()
            if imported > 0:
                logger.info("JSONL migration imported %d events", imported)
        except Exception:
            logger.exception("JSONL migration failed (non-fatal)")

    # Start background card/embedding processor
    _card_processor = BackgroundCardProcessor(
        _event_store, embedding_model=config.embedding_model
    )
    _card_processor.start()

    # Wire audit log to workflow adapter
    workflow_adapter.set_audit_log(_event_store)
    workflow_adapter.set_registry(registry)

    # Initialize MCP connection manager
    connection_manager = ConnectionManager(_event_store, registry)
    mcp_external_adapter = McpExternalAdapter(connection_manager)
    adapters["mcp"] = mcp_external_adapter
    connection_manager.load_from_db()

    # Load tool definitions
    registry.load_from_directory(config.tools_dir)
    _register_mcp_tools()

    # Set up hot-reload
    if config.watch_tools_dir:
        registry.on_change(_register_mcp_tools)
        registry.start_watching(config.tools_dir)

    # Create LLM provider and router (non-fatal — user can configure later via Settings)
    try:
        provider = _create_llm_provider(config)
        llm_router = LLMRouter(provider, registry)
        logger.info("LLM router initialized with provider: %s", provider.name)
    except Exception as e:
        llm_router = None
        logger.warning(
            "LLM provider not available: %s. "
            "Chat will be disabled until an API key is configured via Settings.",
            e,
        )

    # Start pipe server on background thread
    pipe_server = PipeServer(
        pipe_name=config.pipe_name,
        timeout=config.pipe_timeout_seconds,
        on_connect=_on_pipe_connect,
        on_disconnect=_on_pipe_disconnect,
    )
    pipe_server.start()

    # Auto-connect enabled MCP connections in background
    def _bg_connect():
        import asyncio as _asyncio
        loop = _asyncio.new_event_loop()
        try:
            loop.run_until_complete(connection_manager.connect_all_enabled())
        except Exception:
            logger.exception("Auto-connect MCP connections failed (non-fatal)")
        finally:
            loop.close()
    threading.Thread(target=_bg_connect, daemon=True, name="mcp-autoconnect").start()

    logger.info(
        "Revit Orchestrator MCP server initialized with %d tools",
        len(registry.list_tool_names()),
    )


def shutdown() -> None:
    """Gracefully shut down all server components."""
    logger.info("Shutting down...")
    _shutdown_event.set()

    if connection_manager is not None:
        try:
            import asyncio as _asyncio
            loop = _asyncio.new_event_loop()
            loop.run_until_complete(connection_manager.disconnect_all())
            loop.close()
        except Exception:
            logger.debug("Error disconnecting MCP connections", exc_info=True)

    if pipe_server is not None:
        try:
            pipe_server.stop()
        except Exception:
            logger.debug("Error stopping pipe server", exc_info=True)

    if _card_processor is not None:
        try:
            _card_processor.stop()
        except Exception:
            logger.debug("Error stopping card processor", exc_info=True)

    if registry is not None:
        try:
            registry.stop_watching()
        except Exception:
            logger.debug("Error stopping registry watcher", exc_info=True)

    if _event_store is not None:
        try:
            _event_store.close()
        except Exception:
            logger.debug("Error closing event store", exc_info=True)

    logger.info("Shutdown complete")

    # Flush all log handlers so nothing is lost on fast exit
    for handler in logging.getLogger().handlers:
        try:
            handler.flush()
        except Exception:
            pass


def _is_process_alive(pid: int) -> bool:
    """Check whether *pid* is still running (Windows-safe).

    Uses ``kernel32.OpenProcess`` so that ``PermissionError`` (which
    ``os.kill(pid, 0)`` raises on Windows when the caller lacks
    ``PROCESS_QUERY_LIMITED_INFORMATION``) is not mistaken for
    "process does not exist".
    """
    import ctypes
    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
    STILL_ACTIVE = 259
    kernel32 = ctypes.windll.kernel32  # type: ignore[attr-defined]

    handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        err = ctypes.get_last_error()
        # ERROR_INVALID_PARAMETER (87) → PID never existed / invalid
        # ERROR_ACCESS_DENIED (5)     → process exists, we just can't open it
        if err == 5:
            return True  # access denied ⇒ process exists
        return False
    try:
        exit_code = ctypes.c_ulong()
        if kernel32.GetExitCodeProcess(handle, ctypes.byref(exit_code)):
            return exit_code.value == STILL_ACTIVE
        return False
    finally:
        kernel32.CloseHandle(handle)


def run_pipe_only() -> None:
    """Block until a shutdown signal is received (pipe-only mode).

    Monitors:
    - Named Windows kernel event (set by C# host via ``ORCHESTRATOR_SHUTDOWN_EVENT``)
    - Parent process liveness (exits if parent PID dies)
    - SIGINT / SIGTERM / SIGBREAK signals
    """
    logger.info("run_pipe_only: entering (pid=%d, ppid=%d)", os.getpid(), os.getppid())

    # --- Named event from C# host ---
    event_name = os.environ.get("ORCHESTRATOR_SHUTDOWN_EVENT")
    if event_name:
        logger.info("run_pipe_only: will watch named event %s", event_name)

        def _wait_named_event() -> None:
            try:
                import win32event  # type: ignore[import-untyped]
                handle = win32event.OpenEvent(
                    win32event.EVENT_ALL_ACCESS, False, event_name
                )
                win32event.WaitForSingleObject(handle, win32event.INFINITE)
                logger.info("Named shutdown event signalled")
                _shutdown_event.set()
            except Exception:
                logger.debug("Named event watcher failed", exc_info=True)

        t = threading.Thread(target=_wait_named_event, daemon=True, name="shutdown-event")
        t.start()

    # --- Parent PID liveness ---
    ppid = os.getppid()
    if ppid > 1:
        logger.info("run_pipe_only: will watch parent pid %d", ppid)

        def _watch_parent() -> None:
            import time
            while not _shutdown_event.is_set():
                if not _is_process_alive(ppid):
                    logger.info("Parent process %d died, shutting down", ppid)
                    _shutdown_event.set()
                    return
                time.sleep(2)

        t = threading.Thread(target=_watch_parent, daemon=True, name="parent-watcher")
        t.start()

    # --- Signal handlers ---
    def _signal_handler(signum: int, _frame: Any) -> None:
        logger.info("Received signal %s, shutting down", signum)
        _shutdown_event.set()

    signal.signal(signal.SIGINT, _signal_handler)
    signal.signal(signal.SIGTERM, _signal_handler)
    if hasattr(signal, "SIGBREAK"):
        signal.signal(signal.SIGBREAK, _signal_handler)  # type: ignore[attr-defined]

    # Block until shutdown
    logger.info("run_pipe_only: blocking on shutdown event")
    _shutdown_event.wait()
    logger.info("run_pipe_only: shutdown event set, exiting")


def run_mcp() -> None:
    """Run the MCP stdio transport (standalone mode)."""
    if mcp is None:
        raise RuntimeError("Server not initialized — call init() first")
    mcp.run()
