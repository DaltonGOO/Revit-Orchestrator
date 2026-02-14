"""Vector similarity search over events and episodes.

Tries sqlite-vss first, falls back to brute-force cosine scan.
The brute-force path is fine for local stores under ~100k vectors.
"""

from __future__ import annotations

import logging
from typing import Any

from .embeddings import EmbeddingEngine
from .sqlite_store import SqliteEventStore

logger = logging.getLogger(__name__)


class VectorSearch:
    """Search events and episodes by embedding similarity."""

    def __init__(
        self,
        store: SqliteEventStore,
        engine: EmbeddingEngine,
    ) -> None:
        self._store = store
        self._engine = engine

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------

    def find_similar_events(
        self,
        query_embedding: bytes,
        limit: int = 10,
        min_similarity: float = 0.3,
        tool_name: str | None = None,
        status: str | None = None,
        episode_id: str | None = None,
    ) -> list[tuple[dict[str, Any], float]]:
        """Find events similar to *query_embedding*.

        Returns list of ``(event_dict, similarity_score)`` ordered by
        descending similarity.
        """
        clauses: list[str] = ["e.embedding IS NOT NULL"]
        params: list[Any] = []

        if tool_name:
            clauses.append("e.tool_name = ?")
            params.append(tool_name)
        if status:
            clauses.append("e.status = ?")
            params.append(status)
        if episode_id:
            clauses.append("e.episode_id = ?")
            params.append(episode_id)

        where = " WHERE " + " AND ".join(clauses)
        sql = f"SELECT * FROM events e{where}"

        rows = self._store._conn.execute(sql, params).fetchall()
        results: list[tuple[dict[str, Any], float]] = []

        for row in rows:
            emb = row["embedding"]
            if not emb:
                continue
            sim = self._engine.cosine_similarity(query_embedding, emb)
            if sim >= min_similarity:
                results.append((dict(row), sim))

        results.sort(key=lambda x: x[1], reverse=True)
        return results[:limit]

    def find_similar_episodes(
        self,
        query_embedding: bytes,
        limit: int = 10,
        min_similarity: float = 0.3,
        doc_guid: str | None = None,
        outcome: str | None = None,
    ) -> list[tuple[dict[str, Any], float]]:
        """Find episodes similar to *query_embedding*."""
        clauses: list[str] = ["ep.embedding IS NOT NULL"]
        params: list[Any] = []

        if doc_guid:
            clauses.append("ep.doc_guid = ?")
            params.append(doc_guid)
        if outcome:
            clauses.append("ep.outcome = ?")
            params.append(outcome)

        where = " WHERE " + " AND ".join(clauses)
        sql = f"SELECT * FROM episodes ep{where}"

        rows = self._store._conn.execute(sql, params).fetchall()
        results: list[tuple[dict[str, Any], float]] = []

        for row in rows:
            emb = row["embedding"]
            if not emb:
                continue
            sim = self._engine.cosine_similarity(query_embedding, emb)
            if sim >= min_similarity:
                results.append((dict(row), sim))

        results.sort(key=lambda x: x[1], reverse=True)
        return results[:limit]

    def search_by_text(
        self,
        query: str,
        search_type: str = "episodes",
        limit: int = 10,
        min_similarity: float = 0.3,
        **filters: Any,
    ) -> list[tuple[dict[str, Any], float]]:
        """Convenience wrapper: embed *query* then search."""
        emb = self._engine.embed(query)
        if search_type == "events":
            return self.find_similar_events(
                emb, limit=limit, min_similarity=min_similarity, **filters
            )
        return self.find_similar_episodes(
            emb, limit=limit, min_similarity=min_similarity, **filters
        )
