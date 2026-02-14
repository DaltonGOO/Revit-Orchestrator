"""Handler for orchestrator.explain_failure tool."""

from __future__ import annotations

import json
from typing import Any

from orchestrator.dispatcher.result import ToolResult


async def execute(args: dict[str, Any], **kwargs) -> ToolResult:
    """Analyse a failure by searching for similar failures and resolutions."""
    from orchestrator.server import _event_store

    if _event_store is None:
        return ToolResult.fail("HANDLER_ERROR", "Event store not available")

    error_code = args.get("error_code", "")
    tool_name = args.get("tool_name")
    context_text = args.get("context", "")

    if not error_code:
        return ToolResult.fail("SCHEMA_VALIDATION_FAILED", "error_code is required")

    # Find past failures with the same error code
    conn = _event_store._conn
    clauses = ["e.status = 'failed'", "e.error_code = ?"]
    params: list[Any] = [error_code]

    if tool_name:
        clauses.append("e.tool_name = ?")
        params.append(tool_name)

    where = " WHERE " + " AND ".join(clauses)
    failed_rows = conn.execute(
        f"SELECT * FROM events e{where} ORDER BY e.ts DESC LIMIT 20",
        params,
    ).fetchall()

    similar_failures = []
    for row in failed_rows:
        error_info = None
        if row["error_json"]:
            try:
                error_info = json.loads(row["error_json"])
            except (json.JSONDecodeError, TypeError):
                error_info = row["error_json"]

        similar_failures.append({
            "event_id": row["id"],
            "episode_id": row["episode_id"],
            "tool_name": row["tool_name"],
            "error_code": row["error_code"],
            "error": error_info,
            "duration_ms": row["duration_ms"],
            "card_text": row["card_text"] or "",
            "ts": row["ts"],
        })

    # For each failed event, look ahead in the same episode to find
    # successful events that may represent a resolution
    resolutions: list[dict[str, Any]] = []
    seen_episodes: set[str] = set()

    for failure in similar_failures:
        ep_id = failure["episode_id"]
        if ep_id in seen_episodes:
            continue
        seen_episodes.add(ep_id)

        # Get events after the failure in the same episode
        subsequent = conn.execute(
            """SELECT * FROM events
               WHERE episode_id = ? AND seq > (
                   SELECT seq FROM events WHERE id = ?
               ) AND status = 'completed'
               ORDER BY seq LIMIT 5""",
            (ep_id, failure["event_id"]),
        ).fetchall()

        for row in subsequent:
            resolutions.append({
                "event_id": row["id"],
                "episode_id": row["episode_id"],
                "tool_name": row["tool_name"],
                "card_text": row["card_text"] or "",
                "ts": row["ts"],
                "preceded_failure": failure["event_id"],
            })

    # If ML is available, also search by semantic similarity
    semantic_matches: list[dict[str, Any]] = []
    from orchestrator.store.embeddings import EmbeddingEngine

    if EmbeddingEngine.available() and context_text:
        try:
            from orchestrator.store.vector_search import VectorSearch

            engine = EmbeddingEngine()
            vsearch = VectorSearch(_event_store, engine)
            search_query = f"ERROR: {error_code} {tool_name or ''} {context_text}"
            matches = vsearch.search_by_text(
                search_query, search_type="events", limit=5
            )
            for item, score in matches:
                semantic_matches.append({
                    "event_id": item.get("id", ""),
                    "card_text": item.get("card_text", ""),
                    "similarity": round(score, 4),
                    "status": item.get("status", ""),
                })
        except Exception:
            pass  # Non-fatal

    return ToolResult.ok({
        "similar_failures": similar_failures[:10],
        "resolutions": resolutions[:10],
        "semantic_matches": semantic_matches,
        "error_code": error_code,
        "total_failures_found": len(similar_failures),
    })
