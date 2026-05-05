# -*- coding: utf-8 -*-
"""Revit Orchestrator pyRevit extension — startup hook.

Registers HTTP routes that the C# add-in calls into. The endpoints execute
user-supplied Python tools inside pyRevit's IronPython engine, where the
Revit API, sys.path, codecs, transactions, and the document context are
already correctly bootstrapped — so a tool script just imports
``Autodesk.Revit.DB`` and calls ``run(uiapp, doc, inputs)``.

See ``tools/pyrevit/README.md`` for the tool authoring convention.
"""

import json
import logging
import os
import sys
import traceback

from pyrevit import routes


logger = logging.getLogger(__name__)


api = routes.API("orchestrator")


@api.route("/ping/", methods=["GET"])
def ping(doc):
    """Health check used by the C# adapter to verify the extension is alive."""
    return routes.make_response(data={
        "ok": True,
        "doc_title": getattr(doc, "Title", "") if doc else "",
    })


@api.route("/run_script/", methods=["POST"])
def run_script(uiapp, doc, request):
    """Run a tool script defined as ``def run(uiapp, doc, inputs) -> dict``.

    Expected request body:
        {"script_path": "<absolute path to .py>", "inputs": { ... }}

    Returns whatever dict ``run`` returned. If the script raises, returns
    ``{"error": "...", "traceback": "..."}`` with HTTP 500 so the chat sees
    the failure.
    """
    try:
        data = request.data
        if isinstance(data, str):
            data = json.loads(data)

        script_path = (data or {}).get("script_path", "").strip()
        inputs = (data or {}).get("inputs") or {}

        if not script_path:
            return routes.make_response(
                data={"error": "script_path is required"}, status=400)
        if not os.path.exists(script_path):
            return routes.make_response(
                data={"error": "Script not found: {}".format(script_path)},
                status=400)

        # Make sibling imports work for tool packages.
        script_dir = os.path.dirname(script_path)
        if script_dir and script_dir not in sys.path:
            sys.path.insert(0, script_dir)

        # Compile + execute the file in a fresh namespace so each call is
        # isolated. Inject the orchestrator-side conventions (uiapp, doc,
        # inputs, run sentinel) so user scripts match the documented contract.
        namespace = {
            "__name__": "__orchestrator_tool__",
            "__file__": script_path,
            "uiapp": uiapp,
            "doc": doc,
            "inputs": inputs,
        }

        with open(script_path, "rb") as fh:
            source = fh.read()
        try:
            source_text = source.decode("utf-8")
        except UnicodeDecodeError:
            source_text = source.decode("latin-1")
        code = compile(source_text, script_path, "exec")
        exec(code, namespace)

        run_func = namespace.get("run")
        if not callable(run_func):
            return routes.make_response(
                data={"error": "Script must define `def run(uiapp, doc, inputs)`. "
                               "See tools/pyrevit/README.md."},
                status=400)

        result = run_func(uiapp, doc, inputs)
        return routes.make_response(data=_to_jsonable(result))

    except Exception as exc:
        logger.exception("Tool script failed")
        return routes.make_response(
            data={
                "error": "{}: {}".format(type(exc).__name__, exc),
                "traceback": traceback.format_exc(),
            },
            status=500,
        )


# ───────────────────────────────────────────────────────────────────────────


def _to_jsonable(value):
    """Coerce return values to a JSON-friendly tree.

    Plain dicts/lists pass through. Revit ``Element``s collapse to a small
    summary so the chat doesn't choke on the API graph. Anything else is
    stringified as a fallback.
    """
    if value is None:
        return None
    if isinstance(value, (bool, int, long, float, str)):  # noqa: F821 (IPy 2.7 has long)
        return value
    if isinstance(value, dict):
        return {str(k): _to_jsonable(v) for k, v in value.items()}
    if isinstance(value, (list, tuple, set, frozenset)):
        return [_to_jsonable(v) for v in value]

    # Revit Element-like objects — keep this duck-typed so we don't import
    # the API at module load time (extension.json marks routes as Revit-aware
    # but `pyrevit.routes` does the heavy lifting only when a request arrives).
    name = getattr(value, "Name", None)
    elem_id = getattr(value, "Id", None)
    if elem_id is not None and name is not None:
        return {
            "element_id": getattr(elem_id, "Value", str(elem_id)),
            "name": name,
            "type_name": type(value).__name__,
        }

    return str(value)
