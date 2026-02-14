"""Pattern mining from audit log to detect recurring tool sequences."""

from __future__ import annotations

import logging
from collections import Counter
from dataclasses import dataclass, field
from typing import Any

logger = logging.getLogger(__name__)


@dataclass
class DetectedPattern:
    """A detected recurring pattern of tool calls."""

    sequence: list[str]
    occurrences: int
    example_args: list[dict[str, Any]]
    description: str = ""


class PatternMiner:
    """Mines recurring tool-call patterns from the audit log.

    Groups events by correlation_id into runs, extracts tool-name
    sequences, and uses sliding-window to find repeated subsequences.
    """

    def __init__(self, audit_log: Any) -> None:
        self._audit_log = audit_log

    def find_patterns(
        self,
        min_occurrences: int = 3,
        min_length: int = 2,
        max_length: int = 5,
        lookback_days: int = 30,
    ) -> list[DetectedPattern]:
        """Find recurring tool-call patterns in the audit log.

        Args:
            min_occurrences: Minimum times a pattern must appear.
            min_length: Minimum number of tools in a pattern.
            max_length: Maximum number of tools in a pattern.
            lookback_days: Number of days to look back.

        Returns:
            List of detected patterns sorted by occurrence count (descending).
        """
        # Use SQL-based path if available (SqliteEventStore)
        if hasattr(self._audit_log, "query_episodes"):
            return self._find_patterns_sql(
                min_occurrences, min_length, max_length, lookback_days
            )
        return self._find_patterns_scan(
            min_occurrences, min_length, max_length, lookback_days
        )

    def _find_patterns_sql(
        self,
        min_occurrences: int,
        min_length: int,
        max_length: int,
        lookback_days: int,
    ) -> list[DetectedPattern]:
        """Fast path using SqliteEventStore.query_episodes()."""
        episodes = self._audit_log.query_episodes(limit=500)
        sequences: list[tuple[list[str], list[dict[str, Any]]]] = []

        for ep in episodes:
            events = self._audit_log.get_episode_events(ep["id"])
            started_events = [
                e for e in events
                if e.get("status") == "started" and e.get("tool_name")
            ]
            seq = [e["tool_name"] for e in started_events]
            if len(seq) >= min_length:
                sequences.append((seq, started_events))

        return self._extract_patterns(
            sequences, min_occurrences, min_length, max_length
        )

    def _find_patterns_scan(
        self,
        min_occurrences: int,
        min_length: int,
        max_length: int,
        lookback_days: int,
    ) -> list[DetectedPattern]:
        """Original scan path using read_all_events()."""
        events = self._audit_log.read_all_events(lookback_days=lookback_days)

        # Group events by correlation_id
        runs: dict[str, list[dict[str, Any]]] = {}
        for event in events:
            cid = event.get("correlation_id", "")
            if not cid or event.get("event_type") != "started":
                continue
            runs.setdefault(cid, []).append(event)

        sequences: list[tuple[list[str], list[dict[str, Any]]]] = []
        for cid, run_events in runs.items():
            seq = [e.get("tool_name", "") for e in run_events if e.get("tool_name")]
            if len(seq) >= min_length:
                sequences.append((seq, run_events))

        return self._extract_patterns(
            sequences, min_occurrences, min_length, max_length
        )

    @staticmethod
    def _extract_patterns(
        sequences: list[tuple[list[str], list[dict[str, Any]]]],
        min_occurrences: int,
        min_length: int,
        max_length: int,
    ) -> list[DetectedPattern]:
        """Sliding-window pattern extraction shared by both paths."""
        patterns: dict[str, list[list[dict[str, Any]]]] = {}

        for window_size in range(min_length, max_length + 1):
            for seq, run_events in sequences:
                for i in range(len(seq) - window_size + 1):
                    window = seq[i : i + window_size]
                    key = "|".join(window)
                    if key not in patterns:
                        patterns[key] = []
                    patterns[key].append(run_events[i : i + window_size])

        result: list[DetectedPattern] = []
        seen_keys: set[str] = set()

        for key, occurrences_list in sorted(
            patterns.items(), key=lambda x: len(x[1]), reverse=True
        ):
            if len(occurrences_list) < min_occurrences:
                continue
            if key in seen_keys:
                continue
            seen_keys.add(key)

            tool_names = key.split("|")
            example = occurrences_list[0]
            example_args = [e.get("args", {}) for e in example]

            result.append(
                DetectedPattern(
                    sequence=tool_names,
                    occurrences=len(occurrences_list),
                    example_args=example_args,
                    description=f"Pattern: {' → '.join(tool_names)} ({len(occurrences_list)} occurrences)",
                )
            )

        return result
