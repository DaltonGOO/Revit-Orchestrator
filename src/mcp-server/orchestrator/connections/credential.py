"""DPAPI credential encryption/decryption helpers (Windows only)."""

from __future__ import annotations

import base64
import logging

logger = logging.getLogger(__name__)


def encrypt_credential(plaintext: str) -> str:
    """Encrypt a credential string using DPAPI (CurrentUser scope).

    Returns a base64-encoded encrypted blob.
    """
    try:
        import win32crypt  # type: ignore[import-untyped]
    except ImportError:
        logger.warning("win32crypt not available — storing credential as base64 only")
        return base64.b64encode(plaintext.encode()).decode()

    blob = win32crypt.CryptProtectData(
        plaintext.encode("utf-8"), None, None, None, None, 0
    )
    return base64.b64encode(blob).decode()


def decrypt_credential(enc_b64: str) -> str:
    """Decrypt a DPAPI-encrypted credential from base64.

    Returns the plaintext string.
    """
    if not enc_b64:
        return ""

    try:
        import win32crypt  # type: ignore[import-untyped]
    except ImportError:
        logger.warning("win32crypt not available — decoding base64 only")
        return base64.b64decode(enc_b64).decode()

    blob = base64.b64decode(enc_b64)
    _, data = win32crypt.CryptUnprotectData(blob, None, None, None, 0)
    return data.decode("utf-8")
