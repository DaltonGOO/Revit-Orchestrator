# Revit Orchestrator pyRevit extension

This is a pyRevit extension. It registers HTTP routes that the orchestrator's
C# add-in calls into to run Python tool scripts. pyRevit handles all the
IronPython bootstrap (sys.path, codecs, Revit API references, transactions);
we just dispatch.

## Why

Hosting IronPython ourselves doesn't work reliably. pyRevit already does
this correctly and exposes a routes API for exactly this kind of bridge.

## One-time install

You need to register this extension folder with pyRevit and enable Routes.

1. **Tell pyRevit about this folder.** Easiest way:

       pyrevit extensions paths add "<path-to-repo>\tools"

   …where `<path-to-repo>` is wherever you cloned this repo, e.g.
   `C:\src\Revit-Orchestrator`.

   pyRevit auto-discovers any `*.extension` folder under that path. (Or copy
   the folder into pyRevit's default extensions directory if you prefer.)

2. **Enable pyRevit Routes.** It's off by default for security.

       pyrevit configs routes enable
       pyrevit configs core enabled

   Or in pyRevit UI: `pyRevit → Settings → Routes → Enable`.

3. Restart Revit.

## Verify

Routes start automatically when pyRevit loads. Quick check:

    curl http://localhost:48884/orchestrator/ping/

Should return `{"ok": true, "doc_title": "..."}`.

If `connection refused`: pyRevit Routes is not running. Check pyRevit's log
panel for startup errors.

## Routes

| Method | Path                          | Purpose                                  |
|--------|-------------------------------|------------------------------------------|
| GET    | `/orchestrator/ping/`         | Health check.                            |
| POST   | `/orchestrator/run_script/`   | Run a tool script (see contract below).  |

### `POST /orchestrator/run_script/`

Body:
```json
{ "script_path": "C:\\path\\to\\tool.py", "inputs": { "schedule_name": "..." } }
```

Loads the script, calls `run(uiapp, doc, inputs)`, returns whatever the
function returned as JSON. Errors come back with HTTP 500 plus a Python
traceback in the response body.

The tool authoring contract is documented in
`tools/pyrevit/README.md`.
