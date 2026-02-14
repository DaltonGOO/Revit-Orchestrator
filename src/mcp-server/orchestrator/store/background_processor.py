"""Background thread that generates cards + embeddings for pending events/episodes."""

from __future__ import annotations

import json
import logging
import threading
import time
from typing import Any

from .card_builder import CardBuilder
from .embeddings import EmbeddingEngine
from .sqlite_store import SqliteEventStore

logger = logging.getLogger(__name__)


class BackgroundCardProcessor:
    """Processes events and episodes that lack card_text or embeddings.

    Runs on a background daemon thread.  Call :meth:`start` once and
    :meth:`stop` to shut down.  Also call :meth:`nudge` after an episode
    ends to wake the thread immediately instead of waiting for the next
    poll cycle.
    """

    def __init__(
        self,
        store: SqliteEventStore,
        embedding_model: str = "all-MiniLM-L6-v2",
        poll_interval: float = 30.0,
        batch_size: int = 50,
    ) -> None:
        self._store = store
        self._embedding_model = embedding_model
        self._poll_interval = poll_interval
        self._batch_size = batch_size
        self._thread: threading.Thread | None = None
        self._stop_event = threading.Event()
        self._wake_event = threading.Event()
        self._engine: EmbeddingEngine | None = None

    def start(self) -> None:
        """Start the background processing thread."""
        if self._thread is not None and self._thread.is_alive():
            return
        self._stop_event.clear()
        self._thread = threading.Thread(
            target=self._run, daemon=True, name="card-processor"
        )
        self._thread.start()
        logger.info("Background card processor started")

    def stop(self) -> None:
        """Signal the background thread to stop."""
        self._stop_event.set()
        self._wake_event.set()
        if self._thread:
            self._thread.join(timeout=5.0)
        logger.info("Background card processor stopped")

    def nudge(self) -> None:
        """Wake the background thread to process immediately."""
        self._wake_event.set()

    def process_pending(self, batch_size: int | None = None) -> int:
        """Process a batch of pending events + episodes.  Returns count processed.

        Can be called directly (e.g. from tests) without starting the thread.
        """
        bs = batch_size or self._batch_size
        count = 0
        count += self._process_events(bs)
        count += self._process_episodes(bs)
        return count

    # ------------------------------------------------------------------
    # Internals
    # ------------------------------------------------------------------

    def _get_engine(self) -> EmbeddingEngine | None:
        """Lazy-load the embedding engine."""
        if self._engine is not None:
            return self._engine
        if EmbeddingEngine.available():
            self._engine = EmbeddingEngine(self._embedding_model)
            return self._engine
        return None

    def _run(self) -> None:
        """Background loop."""
        while not self._stop_event.is_set():
            try:
                processed = self.process_pending()
                if processed > 0:
                    logger.debug("Background processor: %d items processed", processed)
            except Exception:
                logger.exception("Error in background card processor")

            # Wait for nudge or poll interval
            self._wake_event.wait(timeout=self._poll_interval)
            self._wake_event.clear()

    def _process_events(self, batch_size: int) -> int:
        """Generate cards + embeddings for events without card_text."""
        rows = self._store._conn.execute(
            "SELECT id, tool_name, args_json, status, delta_json, "
            "context_json, duration_ms, error_code "
            "FROM events WHERE card_text IS NULL LIMIT ?",
            (batch_size,),
        ).fetchall()

        if not rows:
            return 0

        engine = self._get_engine()
        card_texts: list[str] = []
        event_ids: list[str] = []

        for row in rows:
            args = {}
            if row["args_json"]:
                try:
                    args = json.loads(row["args_json"])
                except (json.JSONDecodeError, TypeError):
                    pass

            delta = {}
            if row["delta_json"]:
                try:
                    delta = json.loads(row["delta_json"])
                except (json.JSONDecodeError, TypeError):
                    pass

            context = {}
            if row["context_json"]:
                try:
                    context = json.loads(row["context_json"])
                except (json.JSONDecodeError, TypeError):
                    pass

            card_text, card_features = CardBuilder.build_event_card(
                tool_name=row["tool_name"],
                args=args,
                status=row["status"],
                delta=delta,
                context=context,
                duration_ms=row["duration_ms"] or 0,
                error_code=row["error_code"],
            )

            card_texts.append(card_text)
            event_ids.append(row["id"])

            # Update card text and features (without embedding for now)
            self._store._conn.execute(
                "UPDATE events SET card_text = ?, card_features = ? WHERE id = ?",
                (card_text, card_features, row["id"]),
            )

        # Batch embed if engine available
        if engine and card_texts:
            try:
                embeddings = engine.embed_batch(card_texts)
                for eid, emb in zip(event_ids, embeddings):
                    self._store._conn.execute(
                        "UPDATE events SET embedding = ? WHERE id = ?",
                        (emb, eid),
                    )
            except Exception:
                logger.exception("Failed to embed event cards")

        self._store._conn.commit()
        return len(rows)

    def _process_episodes(self, batch_size: int) -> int:
        """Generate cards + embeddings for episodes without card_text."""
        rows = self._store._conn.execute(
            "SELECT * FROM episodes WHERE card_text IS NULL "
            "AND ended_at IS NOT NULL LIMIT ?",
            (batch_size,),
        ).fetchall()

        if not rows:
            return 0

        engine = self._get_engine()
        card_texts: list[str] = []
        episode_ids: list[str] = []

        for row in rows:
            episode = dict(row)
            events = self._store.get_episode_events(episode["id"])

            card_text, card_features = CardBuilder.build_episode_card(episode, events)
            card_texts.append(card_text)
            episode_ids.append(episode["id"])

            self._store._conn.execute(
                "UPDATE episodes SET card_text = ?, card_features = ? WHERE id = ?",
                (card_text, card_features, episode["id"]),
            )

        if engine and card_texts:
            try:
                embeddings = engine.embed_batch(card_texts)
                for eid, emb in zip(episode_ids, embeddings):
                    self._store._conn.execute(
                        "UPDATE episodes SET embedding = ? WHERE id = ?",
                        (emb, eid),
                    )
            except Exception:
                logger.exception("Failed to embed episode cards")

        self._store._conn.commit()
        return len(rows)
