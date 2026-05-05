// Find views matching a name pattern and/or view type. Used as the
// "discovery" half of sheet-creation chains: list views, then feed the
// IDs into create_sheets_from_views.
//
// Inputs:
//   "name_contains" (str, optional) — case-insensitive substring filter
//   "type"          (str, optional) — view type filter (e.g. "Elevation")

var pattern = ((string)inputs.GetValueOrDefault("name_contains", "")).ToLowerInvariant();
var typeFilter = ((string)inputs.GetValueOrDefault("type", "")).ToLowerInvariant();

var views = new FilteredElementCollector(doc)
    .OfClass(typeof(View))
    .Cast<View>()
    .Where(v => !v.IsTemplate
                && !(v is ViewSchedule)
                && !(v is ViewSheet))
    .Where(v => string.IsNullOrEmpty(pattern)
                || v.Name.ToLowerInvariant().Contains(pattern))
    .Where(v => string.IsNullOrEmpty(typeFilter)
                || v.ViewType.ToString().ToLowerInvariant().Contains(typeFilter))
    .OrderBy(v => v.ViewType.ToString())
    .ThenBy(v => v.Name)
    .Select(v => new Dictionary<string, object>
    {
        ["element_id"] = (long)v.Id.Value,
        ["name"] = v.Name,
        ["type"] = v.ViewType.ToString(),
        ["scale"] = v.Scale,
    })
    .ToList();

return new Dictionary<string, object>
{
    ["count"] = views.Count,
    ["views"] = views,
};
