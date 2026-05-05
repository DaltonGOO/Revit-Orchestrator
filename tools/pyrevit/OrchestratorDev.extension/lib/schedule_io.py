# -*- coding: utf-8 -*-
"""Helpers shared by the Export/Import Schedule pushbuttons.

Round-trip a Revit ViewSchedule's per-element parameter values through JSON.
Element identity is preserved by ElementId, so import only works against the
same model the export came from.
"""

from Autodesk.Revit.DB import (
    BuiltInParameter,
    ElementId,
    FilteredElementCollector,
    StorageType,
    ViewSchedule,
)


# ---------- ElementId compat (Revit <2024 IntegerValue, >=2024 Value) ----------

def eid_to_int(element_id):
    try:
        return int(element_id.Value)
    except AttributeError:
        return int(element_id.IntegerValue)


def int_to_eid(value):
    # ElementId(long) works on Revit 2024+, ElementId(int) on older.
    try:
        return ElementId(long(value))  # noqa: F821 (IronPython 2)
    except NameError:
        return ElementId(int(value))


# ---------- Schedule discovery ----------

def list_user_schedules(doc):
    """Return non-template, non-revision schedules sorted by name."""
    schedules = FilteredElementCollector(doc).OfClass(ViewSchedule).ToElements()
    out = []
    for s in schedules:
        if s.IsTemplate:
            continue
        try:
            if s.IsTitleblockRevisionSchedule:
                continue
        except Exception:
            pass
        out.append(s)
    out.sort(key=lambda x: x.Name)
    return out


# ---------- Parameter value (de)serialization ----------

def param_to_jsonable(param):
    """Read a Parameter into a JSON-friendly dict {storage, value}."""
    if param is None:
        return None
    st = param.StorageType
    if st == StorageType.String:
        return {"storage": "string", "value": param.AsString()}
    if st == StorageType.Integer:
        return {"storage": "integer", "value": param.AsInteger()}
    if st == StorageType.Double:
        return {
            "storage": "double",
            "value": param.AsDouble(),  # internal units
            "display": param.AsValueString(),
        }
    if st == StorageType.ElementId:
        eid = param.AsElementId()
        return {"storage": "elementid", "value": eid_to_int(eid) if eid else -1}
    return {"storage": "none", "value": None}


def apply_jsonable_to_param(param, payload):
    """Write a {storage, value} payload back to a Parameter.

    Returns (changed, reason). reason is None on success, otherwise a string.
    """
    if param is None:
        return False, "no parameter"
    if param.IsReadOnly:
        return False, "read-only"
    if payload is None or "storage" not in payload:
        return False, "no payload"

    storage = payload.get("storage")
    value = payload.get("value")

    try:
        if storage == "string":
            current = param.AsString()
            if current == value:
                return False, None
            param.Set(value if value is not None else "")
            return True, None
        if storage == "integer":
            if value is None:
                return False, "null int"
            if param.AsInteger() == int(value):
                return False, None
            param.Set(int(value))
            return True, None
        if storage == "double":
            if value is None:
                return False, "null double"
            if abs(param.AsDouble() - float(value)) < 1e-9:
                return False, None
            param.Set(float(value))
            return True, None
        if storage == "elementid":
            new_eid = int_to_eid(value)
            current = param.AsElementId()
            if current is not None and eid_to_int(current) == int(value):
                return False, None
            param.Set(new_eid)
            return True, None
    except Exception as exc:
        return False, "set failed: {}".format(exc)
    return False, "unknown storage: {}".format(storage)


# ---------- Schedule -> dict ----------

def collect_schedule_field_names(schedule):
    """Field display names in column order (all fields, including hidden)."""
    definition = schedule.Definition
    names = []
    for i in range(definition.GetFieldCount()):
        names.append(definition.GetField(i).GetName())
    return names


def collect_schedule_elements(doc, schedule):
    """Elements that pass the schedule's filter (order is collector order)."""
    return list(FilteredElementCollector(doc, schedule.Id).ToElements())


def build_export_payload(doc, schedule):
    field_names = collect_schedule_field_names(schedule)
    elements = collect_schedule_elements(doc, schedule)

    rows = []
    for elem in elements:
        row = {
            "element_id": eid_to_int(elem.Id),
            "category": elem.Category.Name if elem.Category else None,
            "values": {},
        }
        for name in field_names:
            param = elem.LookupParameter(name)
            row["values"][name] = param_to_jsonable(param)
        rows.append(row)

    return {
        "schedule_name": schedule.Name,
        "schedule_id": eid_to_int(schedule.Id),
        "fields": field_names,
        "rows": rows,
    }
