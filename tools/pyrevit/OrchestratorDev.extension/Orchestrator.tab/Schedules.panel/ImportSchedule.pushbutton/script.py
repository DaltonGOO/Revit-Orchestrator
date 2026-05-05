# -*- coding: utf-8 -*-
"""Import a JSON file produced by Export Schedule and write parameters back.

Matches rows to elements by ElementId. Read-only fields, calculated fields, and
elements that no longer exist are skipped and reported in the run summary.
"""

import io
import json
import sys

from Autodesk.Revit.DB import Transaction

from pyrevit import forms, revit, script

from schedule_io import apply_jsonable_to_param, int_to_eid


def main():
    doc = revit.doc
    output = script.get_output()

    in_path = forms.pick_file(file_ext="json", title="Select schedule JSON to import")
    if not in_path:
        sys.exit()

    with io.open(in_path, "r", encoding="utf-8") as fh:
        payload = json.loads(fh.read())

    rows = payload.get("rows") or []
    if not rows:
        forms.alert("File contains no rows.", exitscript=True)

    confirm = forms.alert(
        "Import will write parameters from:\n{}\n\nSchedule: {}\nRows: {}\n\nProceed?".format(
            in_path, payload.get("schedule_name", "?"), len(rows)
        ),
        ok=False,
        yes=True,
        no=True,
    )
    if not confirm:
        sys.exit()

    changed = 0
    skipped_missing = 0
    skipped_param = 0
    failures = []

    t = Transaction(doc, "Import Schedule Data")
    t.Start()
    try:
        for row in rows:
            eid_value = row.get("element_id")
            if eid_value is None:
                skipped_missing += 1
                continue
            elem = doc.GetElement(int_to_eid(eid_value))
            if elem is None:
                skipped_missing += 1
                continue
            for field_name, payload_value in (row.get("values") or {}).items():
                param = elem.LookupParameter(field_name)
                ok, reason = apply_jsonable_to_param(param, payload_value)
                if ok:
                    changed += 1
                elif reason in (None, "no parameter", "read-only"):
                    skipped_param += 1
                else:
                    failures.append((eid_value, field_name, reason))
        t.Commit()
    except Exception:
        if t.HasStarted() and not t.HasEnded():
            t.RollBack()
        raise

    output.print_md("**Imported** `{}`".format(payload.get("schedule_name", "?")))
    output.print_md("- parameters changed: {}".format(changed))
    output.print_md("- parameters skipped (missing/read-only/no-op): {}".format(skipped_param))
    output.print_md("- rows skipped (element not found): {}".format(skipped_missing))
    if failures:
        output.print_md("**Failures ({}):**".format(len(failures)))
        for eid_value, field_name, reason in failures[:50]:
            output.print_md("- element {} / `{}` -> {}".format(eid_value, field_name, reason))
        if len(failures) > 50:
            output.print_md("- ...and {} more".format(len(failures) - 50))


if __name__ == "__main__":
    main()
