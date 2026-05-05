# -*- coding: utf-8 -*-
"""Export a Revit schedule to JSON.

Revit Orchestrator tool. The function `run` is the entry point - see
tools/pyrevit/README.md for the full convention.

Inputs (passed as the `inputs` dict):
    schedule_name (str): name of the schedule to export. If omitted, the
        return value lists all available schedules so the caller can pick one.
    output_path (str, optional): file path to write the JSON to.
        Defaults to:
            <Documents>/RevitOrchestrator/<schedule>_<YYYYMMDD-HHMMSS>.json
        so every run produces a verifiable file even when the caller didn't
        supply a path.

Returns:
    dict with `schedule`, `headers`, `rows`, `row_count`, and `file` (the
    path the JSON was actually written to). On lookup failure, returns
    `{"error": ..., "available_schedules": [...]}` so the caller can retry
    with a valid name without a separate round trip.
"""

import datetime
import io
import json
import os
import re

from Autodesk.Revit.DB import (
    FilteredElementCollector,
    SectionType,
    ViewSchedule,
)


def run(uiapp, doc, inputs):
    schedule_name = inputs.get("schedule_name")
    output_path = inputs.get("output_path")

    user_schedules = sorted(
        (s for s in FilteredElementCollector(doc).OfClass(ViewSchedule).ToElements()
         if not s.IsTemplate),
        key=lambda s: s.Name,
    )

    if not schedule_name:
        return {
            "error": "schedule_name is required",
            "available_schedules": [s.Name for s in user_schedules],
        }

    selected = next((s for s in user_schedules if s.Name == schedule_name), None)
    if selected is None:
        return {
            "error": "Schedule '{}' not found".format(schedule_name),
            "available_schedules": [s.Name for s in user_schedules],
        }

    body = selected.GetTableData().GetSectionData(SectionType.Body)

    # First row is the header row - pull headers, then read data rows.
    headers = [selected.GetCellText(SectionType.Body, 0, c)
               for c in range(body.NumberOfColumns)]

    rows = []
    for r in range(1, body.NumberOfRows):
        row = {}
        for c in range(body.NumberOfColumns):
            key = headers[c] if c < len(headers) and headers[c] else "col_{}".format(c)
            row[key] = selected.GetCellText(SectionType.Body, r, c)
        rows.append(row)

    # Resolve the output path. We always write to disk so the user has a
    # verifiable artefact even when the LLM didn't supply a path.
    out_path = output_path or _default_output_path(selected.Name)
    parent = os.path.dirname(out_path)
    if parent and not os.path.isdir(parent):
        os.makedirs(parent)

    payload = {
        "schedule": selected.Name,
        "headers": headers,
        "rows": rows,
        "row_count": len(rows),
    }

    with io.open(out_path, "w", encoding="utf-8") as fh:
        fh.write(json.dumps(payload, indent=2, ensure_ascii=False))

    payload["file"] = out_path
    return payload


def _default_output_path(schedule_name):
    """Build a safe, timestamped path under <Documents>/RevitOrchestrator/."""
    documents = os.path.join(os.path.expanduser("~"), "Documents")
    out_dir = os.path.join(documents, "RevitOrchestrator")
    safe_name = re.sub(r"[\\/:*?\"<>|]", "_", schedule_name).strip() or "schedule"
    timestamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    return os.path.join(out_dir, "{}_{}.json".format(safe_name, timestamp))
