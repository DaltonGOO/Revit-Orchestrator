"""Entity identity tracking across projects and sessions."""

from __future__ import annotations

import hashlib
import json
import logging
import uuid
from typing import Any, Optional

from .sqlite_store import SqliteEventStore

logger = logging.getLogger(__name__)


class EntityTracker:
    """Maintains a two-level identity for Revit elements.

    Level 1 — **Element identity**: ``(doc_guid, unique_id)`` plus a
    ``type_fingerprint`` derived from family/type/category/key params.

    Level 2 — **Canonical reference**: a cross-project string of the form
    ``category:type:bucketed_params:connector_sig`` that allows matching
    *similar* elements across different documents.
    """

    def __init__(self, store: SqliteEventStore) -> None:
        self._store = store

    # ------------------------------------------------------------------
    # Fingerprints
    # ------------------------------------------------------------------

    @staticmethod
    def compute_type_fingerprint(
        family: str,
        type_name: str,
        category: str,
        key_params: dict[str, Any] | None = None,
    ) -> str:
        """Deterministic SHA-256[:16] from family + type + category + key params."""
        parts = [
            family or "",
            type_name or "",
            category or "",
            json.dumps(key_params or {}, sort_keys=True, default=str),
        ]
        raw = "|".join(parts).lower()
        return hashlib.sha256(raw.encode()).hexdigest()[:16]

    @staticmethod
    def compute_canonical_ref(
        category: str,
        type_name: str,
        bucketed_params: dict[str, Any] | None = None,
        connector_sig: str = "",
    ) -> str:
        """Build a cross-project canonical reference string.

        Format: ``category:type:param_hash:connector_sig``
        """
        param_hash = ""
        if bucketed_params:
            raw = json.dumps(bucketed_params, sort_keys=True, default=str).lower()
            param_hash = hashlib.sha256(raw.encode()).hexdigest()[:8]
        parts = [
            (category or "").lower(),
            (type_name or "").lower(),
            param_hash,
            (connector_sig or "").lower(),
        ]
        return ":".join(parts)

    # ------------------------------------------------------------------
    # CRUD
    # ------------------------------------------------------------------

    def upsert(
        self,
        doc_guid: str,
        element_unique_id: str,
        category: str = "",
        family_name: str = "",
        type_name: str = "",
        key_params: dict[str, Any] | None = None,
        connector_sig: str = "",
        geometry_fingerprint: str = "",
        meta: dict[str, Any] | None = None,
    ) -> str:
        """Create or update an entity.  Returns the entity ID."""
        type_fp = self.compute_type_fingerprint(
            family_name, type_name, category, key_params
        )
        canonical = self.compute_canonical_ref(
            category, type_name, key_params, connector_sig
        )

        conn = self._store._conn
        row = conn.execute(
            "SELECT id FROM entities WHERE doc_guid = ? AND element_unique_id = ?",
            (doc_guid, element_unique_id),
        ).fetchone()

        now = self._store._conn.execute(
            "SELECT datetime('now')"
        ).fetchone()[0]

        if row:
            entity_id = row["id"]
            conn.execute(
                """UPDATE entities SET
                    category = ?, family_name = ?, type_name = ?,
                    type_fingerprint = ?, geometry_fingerprint = ?,
                    canonical_ref = ?, last_seen = ?, meta_json = ?
                   WHERE id = ?""",
                (
                    category, family_name, type_name,
                    type_fp, geometry_fingerprint,
                    canonical, now,
                    json.dumps(meta) if meta else None,
                    entity_id,
                ),
            )
        else:
            entity_id = str(uuid.uuid4())
            conn.execute(
                """INSERT INTO entities
                   (id, doc_guid, element_unique_id, category, family_name,
                    type_name, type_fingerprint, geometry_fingerprint,
                    canonical_ref, first_seen, last_seen, meta_json)
                   VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                (
                    entity_id, doc_guid, element_unique_id,
                    category, family_name, type_name,
                    type_fp, geometry_fingerprint,
                    canonical, now, now,
                    json.dumps(meta) if meta else None,
                ),
            )
        conn.commit()
        return entity_id

    def find_by_canonical(
        self, canonical_ref: str, limit: int = 10
    ) -> list[dict[str, Any]]:
        """Find entities matching a canonical reference."""
        rows = self._store._conn.execute(
            "SELECT * FROM entities WHERE canonical_ref = ? LIMIT ?",
            (canonical_ref, limit),
        ).fetchall()
        return [dict(r) for r in rows]

    def get(self, entity_id: str) -> Optional[dict[str, Any]]:
        """Fetch an entity by ID."""
        row = self._store._conn.execute(
            "SELECT * FROM entities WHERE id = ?", (entity_id,)
        ).fetchone()
        return dict(row) if row else None

    # ------------------------------------------------------------------
    # Enrichment helpers
    # ------------------------------------------------------------------

    def build_targets_json(
        self,
        doc_guid: str,
        model_changes: dict[str, Any],
    ) -> str:
        """Process model_changes from a ToolResult and upsert entities.

        Returns a JSON array of entity IDs affected by this event.
        """
        entity_ids: list[str] = []

        for change in model_changes.get("created", []):
            uid = change.get("unique_id", "")
            if not uid:
                continue
            eid = self.upsert(
                doc_guid=doc_guid,
                element_unique_id=uid,
                category=change.get("category", ""),
                family_name=change.get("family_name", ""),
                type_name=change.get("type_name", ""),
            )
            entity_ids.append(eid)

        for change in model_changes.get("modified", []):
            uid = change.get("unique_id", "")
            if not uid:
                continue
            eid = self.upsert(
                doc_guid=doc_guid,
                element_unique_id=uid,
                category=change.get("category", ""),
                family_name=change.get("family_name", ""),
                type_name=change.get("type_name", ""),
            )
            entity_ids.append(eid)

        return json.dumps(entity_ids)
