"""Reconciliation mapper for workflow replay type mismatches.

When a recorded workflow references types (piping system types, family types,
host elements) that don't exist in the target model, this mapper:

1. Dispatches read-only resolve commands to discover what's available.
2. If an exact match exists, uses it silently (no prompt).
3. If no exact match, sends a mapping_request to the UI for user reconciliation.
4. Caches resolved mappings within a workflow run for consistency.
"""

from __future__ import annotations

import logging
from typing import Any

from ..pipe.protocol import make_mapping_request

logger = logging.getLogger(__name__)


class ReconciliationMapper:
    """Reconciles type mismatches between recorded workflows and the target model.

    Args:
        dispatcher: The tool dispatcher for sending resolve commands.
        pipe_connection: The pipe connection for sending mapping requests to the UI.
    """

    def __init__(self, dispatcher: Any, pipe_connection: Any) -> None:
        self._dispatcher = dispatcher
        self._pipe = pipe_connection
        # Cache: (mapping_type, recorded_value) → resolved value or None
        self._cache: dict[tuple[str, str], dict[str, Any] | None] = {}

    async def reconcile_step(self, step: dict[str, Any]) -> dict[str, Any] | None:
        """Reconcile a workflow step's arguments before dispatch.

        Returns modified args dict, or None if the user cancelled.
        """
        tool_name = step.get("tool", "")
        args = dict(step.get("args", {}))

        if tool_name == "revit.create_element":
            return await self._reconcile_mep_element(args)
        elif tool_name == "revit.place_family":
            return await self._reconcile_family_instance(args)

        # No reconciliation needed for other tools
        return args

    async def _reconcile_mep_element(self, args: dict[str, Any]) -> dict[str, Any] | None:
        """Reconcile system type for MEP element creation."""
        system_type_name = args.get("system_type_name")
        classification = args.get("mep_system_classification")

        # No system identity captured — nothing to reconcile
        if not system_type_name and not classification:
            return args

        # Determine the kind of system type
        category = (args.get("category") or "").lower()
        if "duct" in category:
            kind = "mechanical"
        else:
            kind = "piping"

        # Check cache first
        cache_key = ("system_type", system_type_name or classification or "")
        if cache_key in self._cache:
            cached = self._cache[cache_key]
            if cached is None:
                return None  # User previously cancelled
            args["system_type_name"] = cached.get("name", system_type_name)
            return args

        # Dispatch resolve command
        resolve_args: dict[str, Any] = {"kind": kind}
        if system_type_name:
            resolve_args["system_type_name"] = system_type_name
        if classification:
            resolve_args["mep_system_classification"] = classification

        resolve_result = await self._dispatcher.dispatch(
            "revit.resolve_system_type", resolve_args
        )

        if not resolve_result.success:
            logger.warning("Failed to resolve system type: %s", resolve_result.error_message)
            return args  # Proceed with original args, let CreateElementCommand handle it

        data = resolve_result.data or {}
        exact_match = data.get("exact_match", False)
        best_match = data.get("best_match")
        available = data.get("available", [])

        if exact_match and best_match:
            # Exact match found — use silently
            self._cache[cache_key] = best_match
            args["system_type_name"] = best_match.get("name", system_type_name)
            return args

        # No exact match — prompt user
        recorded_value = system_type_name or classification or "unknown"
        result = await self._prompt_user(
            mapping_type="system_type",
            recorded_value=recorded_value,
            available_options=available,
            best_match=best_match,
        )

        if result is None:
            self._cache[cache_key] = None
            return None  # User cancelled

        self._cache[cache_key] = result
        args["system_type_name"] = result.get("name", system_type_name)
        return args

    async def _reconcile_family_instance(self, args: dict[str, Any]) -> dict[str, Any] | None:
        """Reconcile family type and host element for family placement."""
        family_name = args.get("family_name")
        family_type_name = args.get("family_type_name")

        if not family_name and not family_type_name:
            return args

        # --- Reconcile family type ---
        cache_key = ("family_type", f"{family_name}:{family_type_name}")
        if cache_key in self._cache:
            cached = self._cache[cache_key]
            if cached is None:
                return None
            args["family_name"] = cached.get("family_name", family_name)
            args["family_type_name"] = cached.get("type_name", family_type_name)
        else:
            resolve_args: dict[str, Any] = {}
            if family_name:
                resolve_args["family_name"] = family_name
            if family_type_name:
                resolve_args["family_type_name"] = family_type_name

            resolve_result = await self._dispatcher.dispatch(
                "revit.resolve_family_type", resolve_args
            )

            if resolve_result.success:
                data = resolve_result.data or {}
                exact_match = data.get("exact_match", False)
                best_match = data.get("best_match")
                available = data.get("available", [])

                if exact_match and best_match:
                    self._cache[cache_key] = best_match
                    args["family_name"] = best_match.get("family_name", family_name)
                    args["family_type_name"] = best_match.get("type_name", family_type_name)
                else:
                    recorded_value = f"{family_name} : {family_type_name}"
                    result = await self._prompt_user(
                        mapping_type="family_type",
                        recorded_value=recorded_value,
                        available_options=available,
                        best_match=best_match,
                    )
                    if result is None:
                        self._cache[cache_key] = None
                        return None
                    self._cache[cache_key] = result
                    args["family_name"] = result.get("family_name", family_name)
                    args["family_type_name"] = result.get("type_name", family_type_name)

        # --- Reconcile host element (if wall-based or similar) ---
        hosting_behavior = args.get("hosting_behavior", "")
        host_id = args.get("host_element_id")
        host_uid = args.get("host_unique_id")

        if hosting_behavior in ("WallBased", "Wall_based") and (host_id or host_uid):
            host_cache_key = ("host_element", str(host_uid or host_id))
            if host_cache_key in self._cache:
                cached = self._cache[host_cache_key]
                if cached is None:
                    return None
                args["host_element_id"] = cached.get("element_id", host_id)
            else:
                resolve_args = {}
                if host_uid:
                    resolve_args["host_unique_id"] = host_uid
                if host_id:
                    resolve_args["host_element_id"] = host_id
                host_category = args.get("host_category")
                if host_category:
                    resolve_args["host_category"] = host_category
                location = args.get("location")
                if location:
                    resolve_args["location"] = location

                resolve_result = await self._dispatcher.dispatch(
                    "revit.resolve_host", resolve_args
                )

                if resolve_result.success:
                    data = resolve_result.data or {}
                    if data.get("found"):
                        self._cache[host_cache_key] = data
                        args["host_element_id"] = data.get("element_id", host_id)
                    else:
                        candidates = data.get("candidates", [])
                        recorded_value = f"Host {host_category or 'element'} (id={host_id})"
                        result = await self._prompt_user(
                            mapping_type="host_element",
                            recorded_value=recorded_value,
                            available_options=candidates,
                            best_match=None,
                        )
                        if result is None:
                            self._cache[host_cache_key] = None
                            return None
                        self._cache[host_cache_key] = result
                        args["host_element_id"] = result.get("element_id", host_id)

        return args

    async def _prompt_user(
        self,
        mapping_type: str,
        recorded_value: str,
        available_options: list[dict[str, Any]],
        best_match: dict[str, Any] | None,
    ) -> dict[str, Any] | None:
        """Send a mapping_request to the UI and wait for the user's response.

        Returns the selected option dict, or None if cancelled.
        """
        if not self._pipe or not self._pipe.connected:
            logger.warning("No pipe connection for mapping prompt, skipping reconciliation")
            return best_match  # Fall back to best match if no UI available

        msg = make_mapping_request(
            mapping_type=mapping_type,
            recorded_value=recorded_value,
            available_options=available_options,
            best_match=best_match,
        )

        try:
            response = await self._pipe.send_and_wait(msg, timeout=120.0)
            payload = response.get("payload", {})
            action = payload.get("action", "cancel")

            if action == "map":
                return payload.get("selected_option")
            elif action == "create":
                # "Create new" — not yet implemented, treat as proceed with best match
                return best_match
            else:
                return None  # Cancelled
        except Exception:
            logger.exception("Error during mapping request")
            return best_match  # Fall back to best match on error
