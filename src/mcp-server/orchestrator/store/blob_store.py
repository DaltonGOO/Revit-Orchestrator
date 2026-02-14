"""Content-addressed blob storage with optional zstd compression."""

from __future__ import annotations

import hashlib
import logging
from pathlib import Path
from typing import Optional

logger = logging.getLogger(__name__)

# Try to import zstandard; fall back to no-compression if unavailable
try:
    import zstandard as zstd

    _HAS_ZSTD = True
except ImportError:
    _HAS_ZSTD = False

# Magic bytes for detecting zstd-compressed blobs
_ZSTD_MAGIC = b"\x28\xb5\x2f\xfd"


class BlobStore:
    """Content-addressed file store organised as ``sha256/<first2>/<hash>.ext``.

    Deduplicates on write — if a blob with the same SHA-256 already exists the
    write is skipped.  Optionally compresses data with zstandard on write and
    transparently decompresses on read.
    """

    def __init__(self, root_dir: Path) -> None:
        self._root = root_dir
        self._root.mkdir(parents=True, exist_ok=True)

    @property
    def root(self) -> Path:
        return self._root

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------

    def put(
        self,
        data: bytes,
        ext: str = ".bin",
        compress: bool = True,
    ) -> tuple[str, str]:
        """Store *data* and return ``(sha256_hex, relative_path)``.

        If a blob with the same hash already exists the write is skipped
        (content-addressed dedup).  When *compress* is ``True`` and the
        ``zstandard`` package is available the blob is stored compressed.
        """
        sha = hashlib.sha256(data).hexdigest()
        rel = self._rel_path(sha, ext)
        abs_path = self._root / rel

        if abs_path.exists():
            return sha, rel

        abs_path.parent.mkdir(parents=True, exist_ok=True)

        if compress and _HAS_ZSTD:
            cctx = zstd.ZstdCompressor(level=3)
            blob = cctx.compress(data)
        else:
            blob = data

        abs_path.write_bytes(blob)
        logger.debug("Stored blob %s (%d bytes)", rel, len(blob))
        return sha, rel

    def get(self, sha256: str, ext: str = ".bin") -> Optional[bytes]:
        """Retrieve blob by hash.  Returns ``None`` if not found.

        Auto-detects and decompresses zstd blobs transparently.
        """
        rel = self._rel_path(sha256, ext)
        abs_path = self._root / rel

        if not abs_path.exists():
            return None

        raw = abs_path.read_bytes()
        if raw[:4] == _ZSTD_MAGIC and _HAS_ZSTD:
            dctx = zstd.ZstdDecompressor()
            return dctx.decompress(raw)
        return raw

    def exists(self, sha256: str, ext: str = ".bin") -> bool:
        """Check whether a blob with this hash is already stored."""
        return (self._root / self._rel_path(sha256, ext)).exists()

    def path_for(self, sha256: str, ext: str = ".bin") -> Path:
        """Return the absolute path where this hash would be stored."""
        return self._root / self._rel_path(sha256, ext)

    # ------------------------------------------------------------------
    # Internals
    # ------------------------------------------------------------------

    @staticmethod
    def _rel_path(sha256: str, ext: str) -> str:
        """Build ``sha256/<first2>/<hash><ext>``."""
        return f"sha256/{sha256[:2]}/{sha256}{ext}"
