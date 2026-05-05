// Summarise Revit's project warnings (the bell-icon list), grouped by
// description with the most-common ones first. Sample element IDs included
// so the user (or LLM) can drill in.

var warnings = doc.GetWarnings();

var grouped = warnings
    .GroupBy(w => w.GetDescriptionText())
    .OrderByDescending(g => g.Count())
    .Select(g => new Dictionary<string, object>
    {
        ["description"] = g.Key,
        ["count"] = g.Count(),
        ["severity"] = g.First().GetSeverity().ToString(),
        ["sample_element_ids"] = g.SelectMany(w => w.GetFailingElements())
            .Select(id => (long)id.Value).Distinct().Take(5).ToList(),
    })
    .ToList();

return new Dictionary<string, object>
{
    ["total_warnings"] = warnings.Count,
    ["unique_types"]   = grouped.Count,
    ["top_issues"]     = grouped.Take(15).ToList(),
};
