"""Deterministic card-text and feature-dict generation for events and episodes."""

from __future__ import annotations

import json
import logging
from typing import Any

logger = logging.getLogger(__name__)


class CardBuilder:
    """Builds structured card text and feature dictionaries for events and episodes.

    Card text is deterministic structured text (not prose) designed for
    embedding and similarity search.  Feature dicts capture numeric /
    categorical dimensions for filtering and ML feature extraction.
    """

    # ------------------------------------------------------------------
    # Event card
    # ------------------------------------------------------------------

    @staticmethod
    def build_event_card(
        tool_name: str,
        args: dict[str, Any] | None = None,
        status: str = "",
        delta: dict[str, Any] | None = None,
        context: dict[str, Any] | None = None,
        duration_ms: int = 0,
        error_code: str | None = None,
    ) -> tuple[str, str]:
        """Build ``(card_text, card_features_json)`` for a single event.

        Card text format::

            ACTION: revit.create_wall
            CATEGORY: Walls | TYPE: Basic Wall - Generic 200mm
            PARAMS: start=[0,0,0] end=[10,0,0] height=3.0 level=Level 1
            RESULT: completed in 245ms | CHANGES: +1 Wall
            CONTEXT: FloorPlan on Level 1, Revit 2025
        """
        args = args or {}
        delta = delta or {}
        context = context or {}

        lines: list[str] = []

        # ACTION line
        lines.append(f"ACTION: {tool_name}")

        # CATEGORY / TYPE line (extract from args or context)
        cat_parts: list[str] = []
        if args.get("wall_type") or args.get("type_name"):
            type_val = args.get("wall_type") or args.get("type_name") or ""
            cat_parts.append(f"TYPE: {type_val}")
        if args.get("category"):
            cat_parts.insert(0, f"CATEGORY: {args['category']}")

        # Infer category from tool namespace
        ns = tool_name.split(".")[0] if "." in tool_name else ""
        if not cat_parts and ns == "revit":
            action_part = tool_name.split(".")[-1] if "." in tool_name else tool_name
            if "wall" in action_part.lower():
                cat_parts.append("CATEGORY: Walls")
            elif "floor" in action_part.lower():
                cat_parts.append("CATEGORY: Floors")
            elif "duct" in action_part.lower():
                cat_parts.append("CATEGORY: Ducts")
            elif "pipe" in action_part.lower():
                cat_parts.append("CATEGORY: Pipes")

        if cat_parts:
            lines.append(" | ".join(cat_parts))

        # PARAMS line (compact key=value pairs)
        param_parts: list[str] = []
        for k, v in args.items():
            if k.startswith("_"):
                continue
            if isinstance(v, (list, tuple)) and len(v) <= 4:
                param_parts.append(f"{k}={list(v)}")
            elif isinstance(v, dict):
                continue  # skip nested objects
            else:
                param_parts.append(f"{k}={v}")
        if param_parts:
            lines.append(f"PARAMS: {' '.join(param_parts[:8])}")

        # RESULT line
        result_parts: list[str] = []
        if status:
            result_parts.append(status)
        if duration_ms:
            result_parts.append(f"in {duration_ms}ms")
        if error_code:
            result_parts.append(f"ERROR: {error_code}")

        change_parts: list[str] = []
        created = delta.get("created", 0)
        modified = delta.get("modified", 0)
        deleted = delta.get("deleted", 0)
        if created:
            change_parts.append(f"+{created}")
        if modified:
            change_parts.append(f"~{modified}")
        if deleted:
            change_parts.append(f"-{deleted}")
        if change_parts:
            result_parts.append(f"CHANGES: {' '.join(change_parts)}")

        if result_parts:
            lines.append(f"RESULT: {' | '.join(result_parts)}")

        # CONTEXT line
        ctx_parts: list[str] = []
        if context.get("active_view_type"):
            ctx_parts.append(context["active_view_type"])
        if context.get("doc_title"):
            ctx_parts.append(f"in {context['doc_title']}")
        if context.get("revit_version"):
            ctx_parts.append(f"Revit {context['revit_version']}")
        if ctx_parts:
            lines.append(f"CONTEXT: {', '.join(ctx_parts)}")

        card_text = "\n".join(lines)

        # Features dict
        features: dict[str, Any] = {
            "tool_namespace": ns,
            "tool_name": tool_name,
            "action_type": tool_name.split(".")[-1] if "." in tool_name else tool_name,
            "param_count": len(args),
            "duration_ms": duration_ms,
            "status": status,
            "created_count": created,
            "modified_count": modified,
            "deleted_count": deleted,
            "has_error": error_code is not None,
            "is_mep": ns in ("dynamo",) or any(
                k in tool_name.lower() for k in ("duct", "pipe", "cable", "conduit")
            ),
        }

        return card_text, json.dumps(features, default=str)

    # ------------------------------------------------------------------
    # Episode card
    # ------------------------------------------------------------------

    @staticmethod
    def build_episode_card(
        episode: dict[str, Any],
        events: list[dict[str, Any]],
    ) -> tuple[str, str]:
        """Build ``(card_text, card_features_json)`` for an entire episode.

        Episode card format::

            EPISODE: 3 tools in 1.2s | outcome=completed
            TOOLS: revit.create_wall → revit.create_wall → revit.get_element_info
            CATEGORIES: Walls
            CHANGES: +2 created, 0 modified, 0 deleted
            CONTEXT: FloorPlan in MyProject.rvt, Revit 2025
        """
        lines: list[str] = []

        tool_names = [e.get("tool_name", "") for e in events if e.get("tool_name")]
        total_ms = episode.get("total_duration_ms", 0)
        outcome = episode.get("outcome", "unknown")

        lines.append(
            f"EPISODE: {len(tool_names)} tools in {total_ms / 1000:.1f}s | outcome={outcome}"
        )

        if tool_names:
            lines.append(f"TOOLS: {' → '.join(tool_names)}")

        # Aggregate categories from events
        categories: set[str] = set()
        total_created = 0
        total_modified = 0
        total_deleted = 0

        for ev in events:
            delta_raw = ev.get("delta_json")
            if delta_raw:
                try:
                    delta = json.loads(delta_raw) if isinstance(delta_raw, str) else delta_raw
                    total_created += delta.get("created", 0)
                    total_modified += delta.get("modified", 0)
                    total_deleted += delta.get("deleted", 0)
                except (json.JSONDecodeError, TypeError):
                    pass

            # Infer category from tool name
            tn = ev.get("tool_name", "").lower()
            if "wall" in tn:
                categories.add("Walls")
            elif "floor" in tn:
                categories.add("Floors")
            elif "duct" in tn:
                categories.add("Ducts")
            elif "pipe" in tn:
                categories.add("Pipes")

        if categories:
            lines.append(f"CATEGORIES: {', '.join(sorted(categories))}")

        lines.append(
            f"CHANGES: +{total_created} created, {total_modified} modified, {total_deleted} deleted"
        )

        # Context
        ctx_parts: list[str] = []
        if episode.get("doc_title"):
            ctx_parts.append(f"in {episode['doc_title']}")
        if episode.get("revit_version"):
            ctx_parts.append(f"Revit {episode['revit_version']}")
        if ctx_parts:
            lines.append(f"CONTEXT: {', '.join(ctx_parts)}")

        card_text = "\n".join(lines)

        # Features
        features: dict[str, Any] = {
            "tool_count": len(tool_names),
            "unique_tools": len(set(tool_names)),
            "total_duration_ms": total_ms,
            "outcome": outcome,
            "total_created": total_created,
            "total_modified": total_modified,
            "total_deleted": total_deleted,
            "llm_input_tokens": episode.get("llm_input_tokens", 0),
            "llm_output_tokens": episode.get("llm_output_tokens", 0),
            "has_error": outcome == "failed",
        }

        return card_text, json.dumps(features, default=str)
