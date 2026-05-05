// The "do something about it" companion to diagnose_visibility.
// Inputs: { "category": "Ducts" } — turn the category on in V/G and,
// if it's an MEP category, switch view discipline to Coordination so
// MEP elements actually render.

var categoryName = (string)inputs.GetValueOrDefault("category", "Ducts");
var view = doc.ActiveView;

Category category = null;
foreach (Category c in doc.Settings.Categories)
{
    if (c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
    {
        category = c;
        break;
    }
}
if (category == null)
{
    return new Dictionary<string, object>
    {
        ["error"] = "Category '" + categoryName + "' not found",
    };
}

var changes = new List<string>();
using (var t = new Transaction(doc, "Fix visibility for " + categoryName))
{
    t.Start();
    if (view.GetCategoryHidden(category.Id))
    {
        view.SetCategoryHidden(category.Id, false);
        changes.Add("Turned on '" + categoryName + "' in V/G overrides");
    }
    var isMep = categoryName.Contains("Duct")
             || categoryName.Contains("Pipe")
             || categoryName.Contains("Conduit")
             || categoryName.Contains("Cable");
    if (isMep
        && view.Discipline != ViewDiscipline.Coordination
        && view.Discipline != ViewDiscipline.Mechanical)
    {
        view.Discipline = ViewDiscipline.Coordination;
        changes.Add("Set view discipline to Coordination");
    }
    t.Commit();
}

return new Dictionary<string, object>
{
    ["view"] = view.Name,
    ["category"] = categoryName,
    ["fixes_applied"] = changes,
    ["status"] = changes.Count > 0 ? "fixed" : "no changes were needed",
};
