"""Handler for orchestrator.search_similar tool."""

from __future__ import annotations

from typing import Any

from orchestrator.dispatcher.result import ToolResult


async def execute(args: dict[str, Any], **kwargs) -> ToolResult:
    """Search for similar past executions using vector similarity."""
    from orchestrator.server import _event_store

    if _event_store is None:
        return ToolResult.fail("HANDLER_ERROR", "Event store not available")

    query = args.get("query", "")
    if not query:
        return ToolResult.fail("SCHEMA_VALIDATION_FAILED", "query is required")

    search_type = args.get("search_type", "episodes")
    limit = args.get("limit", 10)
    doc_guid = args.get("doc_guid")

    # Check if embedding engine is available
    from orchestrator.store.embeddings import EmbeddingEngine

    if not EmbeddingEngine.available():
        # Fall back to text-based search on card_text
        return _fallback_text_search(
            _event_store, query, search_type, limit, doc_guid
        )

    from orchestrator.store.vector_search import VectorSearch

    engine = EmbeddingEngine()
    vsearch = VectorSearch(_event_store, engine)

    filters: dict[str, Any] = {}
    if doc_guid:
        filters["doc_guid"] = doc_guid

    results = vsearch.search_by_text(
        query,
        search_type=search_type,
        limit=limit,
        **filters,
    )

    output = []
    for item, score in results:
        output.append({
            "id": item.get("id", ""),
            "card_text": item.get("card_text", ""),
            "similarity": round(score, 4),
            "outcome": item.get("outcome", item.get("status", "")),
            "tool_summary": item.get("tool_summary", item.get("tool_name", "")),
        })

    return ToolResult.ok({
        "results": output,
        "total": len(output),
        "search_type": search_type,
    })


def _fallback_text_search(
    store: Any,
    query: str,
    search_type: str,
    limit: int,
    doc_guid: str | None,
) -> ToolResult:
    """Simple text search on card_text when embeddings aren't available."""
    query_lower = query.lower()

    if search_type == "episodes":
        episodes = store.query_episodes(doc_guid=doc_guid, limit=limit * 3)
        results = []
        for ep in episodes:
            card = (ep.get("card_text") or "").lower()
            summary = (ep.get("tool_summary") or "").lower()
            if query_lower in card or query_lower in summary:
                results.append({
                    "id": ep.get("id", ""),
                    "card_text": ep.get("card_text", ""),
                    "similarity": 0.5,  # fixed score for text match
                    "outcome": ep.get("outcome", ""),
                    "tool_summary": ep.get("tool_summary", ""),
                })
                if len(results) >= limit:
                    break
    else:
        events = store.read_all_events(lookback_days=30, limit=limit * 3)
        results = []
        for ev in events:
            card = (ev.get("card_text") or "").lower()
            tool = (ev.get("tool_name") or "").lower()
            if query_lower in card or query_lower in tool:
                results.append({
                    "id": ev.get("event_id", ""),
                    "card_text": ev.get("card_text", ""),
                    "similarity": 0.5,
                    "outcome": ev.get("event_type", ""),
                    "tool_summary": ev.get("tool_name", ""),
                })
                if len(results) >= limit:
                    break

    return ToolResult.ok({
        "results": results,
        "total": len(results),
        "search_type": search_type,
        "note": "Text-based fallback (install ml extras for vector search)",
    })
