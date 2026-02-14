"""Lazy-loaded local embedding engine using sentence-transformers."""

from __future__ import annotations

import logging
import struct
from typing import Optional

logger = logging.getLogger(__name__)

# Singleton model instance (lazily loaded)
_model: Optional[object] = None
_model_name: str = "all-MiniLM-L6-v2"
_EMBEDDING_DIM = 384  # all-MiniLM-L6-v2 output dimension


class EmbeddingEngine:
    """Generate embeddings for card text using sentence-transformers.

    The model (~80 MB) is loaded lazily on first ``embed()`` call.
    Requires the ``[ml]`` optional dependency group.
    """

    def __init__(self, model_name: str = "all-MiniLM-L6-v2") -> None:
        global _model_name
        _model_name = model_name

    @staticmethod
    def _load_model() -> object:
        """Load the sentence-transformers model (singleton)."""
        global _model
        if _model is not None:
            return _model

        try:
            from sentence_transformers import SentenceTransformer

            logger.info("Loading embedding model: %s", _model_name)
            _model = SentenceTransformer(_model_name)
            logger.info("Embedding model loaded (%s)", _model_name)
            return _model
        except ImportError:
            raise ImportError(
                "sentence-transformers is required for embeddings. "
                "Install with: pip install revit-orchestrator[ml]"
            )

    @staticmethod
    def available() -> bool:
        """Check if the ML dependencies are installed."""
        try:
            import sentence_transformers  # noqa: F401
            return True
        except ImportError:
            return False

    def embed(self, text: str) -> bytes:
        """Embed a single text string.

        Returns raw bytes: 384 float32 values in little-endian format
        (384 * 4 = 1536 bytes).
        """
        model = self._load_model()
        vec = model.encode(text, normalize_embeddings=True)  # type: ignore[union-attr]
        return _vec_to_bytes(vec)

    def embed_batch(self, texts: list[str]) -> list[bytes]:
        """Embed a batch of texts.  Returns list of raw-bytes embeddings."""
        if not texts:
            return []
        model = self._load_model()
        vecs = model.encode(texts, normalize_embeddings=True)  # type: ignore[union-attr]
        return [_vec_to_bytes(v) for v in vecs]

    @staticmethod
    def cosine_similarity(a: bytes, b: bytes) -> float:
        """Compute cosine similarity between two embedding byte blobs.

        Since embeddings are L2-normalised, this is simply the dot product.
        """
        va = _bytes_to_vec(a)
        vb = _bytes_to_vec(b)
        return sum(x * y for x, y in zip(va, vb))

    @staticmethod
    def dimension() -> int:
        return _EMBEDDING_DIM


def _vec_to_bytes(vec) -> bytes:
    """Convert a numpy array or list of floats to LE float32 bytes."""
    try:
        # numpy array
        return vec.astype("float32").tobytes()
    except AttributeError:
        # plain list
        return struct.pack(f"<{len(vec)}f", *vec)


def _bytes_to_vec(data: bytes) -> list[float]:
    """Convert LE float32 bytes back to a list of floats."""
    count = len(data) // 4
    return list(struct.unpack(f"<{count}f", data))
