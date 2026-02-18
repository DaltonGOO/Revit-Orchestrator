"""Logging configuration for the Revit Orchestrator server.

Sets up file logging with rotation and stderr output.
Must be called before any other orchestrator imports to avoid
premature logging configuration.
"""

from __future__ import annotations

import logging
import os
import sys
from logging.handlers import RotatingFileHandler
from pathlib import Path

_DEFAULT_LOG_DIR = os.path.join(
    os.environ.get("LOCALAPPDATA", os.path.expanduser("~")),
    "RevitOrchestrator",
    "logs",
)

_LOG_FORMAT = "%(asctime)s [%(levelname)s] %(name)s: %(message)s"
_MAX_BYTES = 10 * 1024 * 1024  # 10 MB
_BACKUP_COUNT = 5


def configure_logging(log_dir: str | None = None) -> Path:
    """Configure root logger with file rotation and stderr output.

    Parameters
    ----------
    log_dir:
        Directory for log files.  Defaults to
        ``%LOCALAPPDATA%/RevitOrchestrator/logs``.

    Returns
    -------
    Path
        The resolved log directory.
    """
    log_path = Path(log_dir) if log_dir else Path(_DEFAULT_LOG_DIR)
    log_path.mkdir(parents=True, exist_ok=True)

    root = logging.getLogger()

    # Clear any handlers that may have been added before this call
    root.handlers.clear()
    root.setLevel(logging.DEBUG)

    # File handler with rotation
    file_handler = RotatingFileHandler(
        log_path / "orchestrator.log",
        maxBytes=_MAX_BYTES,
        backupCount=_BACKUP_COUNT,
        encoding="utf-8",
    )
    file_handler.setLevel(logging.DEBUG)
    file_handler.setFormatter(logging.Formatter(_LOG_FORMAT))
    root.addHandler(file_handler)

    # Stderr handler at INFO level
    stderr_handler = logging.StreamHandler(sys.stderr)
    stderr_handler.setLevel(logging.INFO)
    stderr_handler.setFormatter(logging.Formatter(_LOG_FORMAT))
    root.addHandler(stderr_handler)

    return log_path
