// Architectural QC: find rooms that haven't been placed in the model
// (no location) or are redundant (placed but enclosed area = 0).

using Autodesk.Revit.DB.Architecture;

var rooms = new FilteredElementCollector(doc)
    .OfCategory(BuiltInCategory.OST_Rooms)
    .WhereElementIsNotElementType()
    .Cast<Room>()
    .ToList();

var unplaced = rooms.Where(r => r.Location == null || r.Area == 0)
    .Select(r => new Dictionary<string, object>
    {
        ["element_id"] = (long)r.Id.Value,
        ["number"] = r.Number ?? "",
        ["name"] = r.Name ?? "",
        ["state"] = r.Location == null ? "unplaced" : "redundant",
    })
    .ToList();

return new Dictionary<string, object>
{
    ["total_rooms"] = rooms.Count,
    ["unplaced_or_redundant_count"] = unplaced.Count,
    ["details"] = unplaced.Take(50).ToList(),
};
