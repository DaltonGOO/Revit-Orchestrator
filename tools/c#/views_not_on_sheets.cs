// Documentation QC: find views that aren't placed on any sheet.
// Useful before issuing a print package — orphans are usually working
// views or test mockups left behind.

var allViews = new FilteredElementCollector(doc)
    .OfClass(typeof(View))
    .Cast<View>()
    .Where(v => !v.IsTemplate
                && v.ViewType != ViewType.SystemBrowser
                && v.ViewType != ViewType.ProjectBrowser
                && v.ViewType != ViewType.Internal
                && !(v is ViewSchedule)
                && !(v is ViewSheet))
    .ToList();

var placed = new HashSet<long>();
foreach (var sheet in new FilteredElementCollector(doc)
    .OfClass(typeof(ViewSheet))
    .Cast<ViewSheet>()
    .Where(s => !s.IsTemplate))
{
    foreach (var vp in sheet.GetAllPlacedViews())
        placed.Add(vp.Value);
}

var orphans = allViews
    .Where(v => !placed.Contains(v.Id.Value))
    .Select(v => new Dictionary<string, object>
    {
        ["element_id"] = (long)v.Id.Value,
        ["name"] = v.Name,
        ["type"] = v.ViewType.ToString(),
    })
    .OrderBy(o => (string)o["type"])
    .ThenBy(o => (string)o["name"])
    .ToList();

return new Dictionary<string, object>
{
    ["total_views"]  = allViews.Count,
    ["orphan_count"] = orphans.Count,
    ["orphans"]      = orphans.Take(50).ToList(),
};
