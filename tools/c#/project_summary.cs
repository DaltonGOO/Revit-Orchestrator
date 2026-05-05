// Project intake — answers "what is this model?" in one tool call.

var pi = doc.ProjectInformation;

var levels = new FilteredElementCollector(doc)
    .OfClass(typeof(Level))
    .Cast<Level>()
    .OrderBy(l => l.Elevation)
    .Select(l => new Dictionary<string, object>
    {
        ["name"] = l.Name,
        ["elevation_ft"] = Math.Round(l.Elevation, 2),
    })
    .ToList();

var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))
    .Cast<ViewSheet>().Where(s => !s.IsTemplate).Count();
var schedules = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule))
    .Cast<ViewSchedule>().Where(s => !s.IsTemplate).Count();
var views = new FilteredElementCollector(doc).OfClass(typeof(View))
    .Cast<View>().Where(v => !v.IsTemplate
        && !(v is ViewSchedule) && !(v is ViewSheet)).Count();

double totalArea = new FilteredElementCollector(doc)
    .OfCategory(BuiltInCategory.OST_Rooms)
    .WhereElementIsNotElementType()
    .Cast<SpatialElement>()
    .Where(r => r.Area > 0)
    .Sum(r => r.Area);

var counts = new Dictionary<string, int>();
foreach (var pair in new (string, BuiltInCategory)[]
{
    ("walls",   BuiltInCategory.OST_Walls),
    ("doors",   BuiltInCategory.OST_Doors),
    ("windows", BuiltInCategory.OST_Windows),
    ("rooms",   BuiltInCategory.OST_Rooms),
    ("ducts",   BuiltInCategory.OST_DuctCurves),
    ("pipes",   BuiltInCategory.OST_PipeCurves),
})
{
    counts[pair.Item1] = new FilteredElementCollector(doc)
        .OfCategory(pair.Item2)
        .WhereElementIsNotElementType()
        .GetElementCount();
}

return new Dictionary<string, object>
{
    ["project_name"]    = pi?.Name ?? doc.Title,
    ["project_number"]  = pi?.Number ?? "",
    ["client"]          = pi?.ClientName ?? "",
    ["levels"]          = levels,
    ["total_floor_area_sqft"] = Math.Round(totalArea, 0),
    ["sheets"]          = sheets,
    ["schedules"]       = schedules,
    ["views"]           = views,
    ["element_counts"]  = counts,
};
