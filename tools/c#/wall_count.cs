// Count walls in the active document.
//
// Demonstrates the Revit Orchestrator C# tool convention:
//   - The whole script is just a few lines of code (no class boilerplate).
//   - `uiapp`, `doc`, `inputs` are globals provided by the host.
//   - Return a `Dictionary<string, object>` as the tool result.
//
// See tools/c#/README.md for the full contract.

var walls = new FilteredElementCollector(doc)
    .OfClass(typeof(Wall))
    .Cast<Wall>()
    .ToList();

return new Dictionary<string, object>
{
    ["wall_count"] = walls.Count,
    ["walls"] = walls
        .Take(50)
        .Select(w => new Dictionary<string, object>
        {
            ["id"] = (long)w.Id.Value,
            ["name"] = w.Name,
            ["type"] = doc.GetElement(w.GetTypeId())?.Name ?? "",
            ["level"] = doc.GetElement(w.LevelId)?.Name ?? "",
        })
        .ToList(),
};
