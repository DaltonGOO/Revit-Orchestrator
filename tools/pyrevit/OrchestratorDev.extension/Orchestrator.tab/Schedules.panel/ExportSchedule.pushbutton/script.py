# -*- coding: utf-8 -*-
"""Export a schedule's rows + per-element parameter values to JSON."""

import io
import json
import os
import sys

from pyrevit import forms, revit, script

from schedule_io import build_export_payload, list_user_schedules


def main():
    doc = revit.doc
    output = script.get_output()

    schedules = list_user_schedules(doc)
    if not schedules:
        forms.alert("No user schedules found in this model.", exitscript=True)

    selected = forms.SelectFromList.show(
        schedules,
        name_attr="Name",
        title="Select a schedule to export",
        multiselect=False,
    )
    if not selected:
        sys.exit()

    default_name = "{}.json".format(selected.Name.replace("/", "_").replace("\\", "_"))
    out_path = forms.save_file(file_ext="json", default_name=default_name)
    if not out_path:
        sys.exit()

    payload = build_export_payload(doc, selected)

    with io.open(out_path, "w", encoding="utf-8") as fh:
        fh.write(json.dumps(payload, indent=2, ensure_ascii=False))

    output.print_md("**Exported** `{}`".format(selected.Name))
    output.print_md("- rows: {}".format(len(payload["rows"])))
    output.print_md("- fields: {}".format(len(payload["fields"])))
    output.print_md("- file: `{}`".format(out_path))


if __name__ == "__main__":
    main()
