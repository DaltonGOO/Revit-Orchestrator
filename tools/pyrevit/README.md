# Revit Orchestrator Python tools

A Python tool is **one `.py` file** with one function:

```python
def run(uiapp, doc, inputs):
    # ...
    return {"some": "json-serialisable result"}
```

That's it. No bundle folders, no `pyrevit` imports, no ribbon button required.

## What's in scope

| Name      | Type                             | What it is                          |
|-----------|----------------------------------|-------------------------------------|
| `uiapp`   | `Autodesk.Revit.UI.UIApplication`| The running Revit UI application    |
| `doc`     | `Autodesk.Revit.DB.Document`     | The active document                 |
| `inputs`  | `dict`                           | The arguments passed by the caller  |

The orchestrator pre-imports `clr` and adds references to `RevitAPI` and
`RevitAPIUI` before your script runs, so this works:

```python
from Autodesk.Revit.DB import FilteredElementCollector, ViewSchedule
```

Anything in IronPython's standard library (`io`, `json`, `os`, `re`, …) and
on the script's own folder is on `sys.path`. Nothing from pyRevit is.

## What to return

A `dict`. It becomes the tool's result — the LLM sees it as JSON.

* **Success:** any dict shape. Common keys are `message`, `result`, plus
  whatever data is useful to the caller.
* **Failure:** include an `"error"` key with a human-readable string. Add
  context (`"available_schedules"`, `"hint"`, etc.) so the caller can retry
  with corrected inputs without a separate round trip.

You can also return a list, a number, a string, or `None` — they get wrapped
as `{"result": <value>}` automatically.

Revit `Element`s are summarised to `{element_id, name, category, type_name}`.
Don't try to return raw API objects — they aren't JSON-serialisable.

## Converting an existing pyRevit ribbon script

Most pyRevit scripts boil down to:

```python
from pyrevit import revit, forms, script
doc = revit.doc
selected = forms.SelectFromList.show(...)   # interactive
output = script.get_output()                # output panel
output.print_md("done")
```

To make it a Revit Orchestrator tool:

1. **Drop the pyRevit imports.** `revit.doc` becomes `doc` (passed in).
2. **Replace interactive forms.** Anything `forms.SelectFromList.show()` was
   asking the user, accept as an `inputs[...]` field. Return
   `{"error": ..., "available_things": [...]}` when the input is missing —
   the LLM will see the choices and retry.
3. **Replace `script.get_output()`.** Just put text in the returned dict.
4. **Wrap the body in `def run(uiapp, doc, inputs):`.**

Your ribbon button isn't affected — the `.pushbutton/script.py` keeps
running through pyRevit's runtime as before. The orchestrator-driven path
is a separate `.py` file under `tools/pyrevit/`.

## Reference

`export_schedule.py` in this folder is a converted version of the
`ExportSchedule.pushbutton` script. Use it as a template.

## Errors

The orchestrator runs your script in a fresh IronPython engine each time.
Errors surface to the chat in this order:

1. **Tool-call validation** — JSON Schema mismatch on `inputs` (caught by
   the dispatcher before your script even loads).
2. **Script load error** — your script wouldn't import (syntax error,
   missing `def run`, bad `from … import`).
3. **Script runtime error** — your `run()` raised. The exception type,
   message, and the top of the Python stack are reported.
4. **`{"error": "..."}` returned** — treated as a soft failure, error string
   shown to the LLM along with the rest of the dict.

If you need to debug, raise a regular Python exception — the orchestrator
will pass the message and stack frames back through the chat.
