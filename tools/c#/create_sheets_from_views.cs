// Crowd-pleaser: one new sheet per supplied view, automatically numbered.
// Whole batch wrapped in a single Transaction so it's one undo step.
//
// Inputs:
//   "view_ids"      (list of int)  — IDs from list_views
//   "sheet_prefix"  (str, default "A-")
//   "start_number"  (int, default 101)
//
// Returns the list of created sheets so the caller can show the user what
// just landed in the Project Browser.

var viewIdsRaw = inputs["view_ids"] as System.Collections.IEnumerable;
if (viewIdsRaw == null)
{
    return new Dictionary<string, object>
    {
        ["error"] = "view_ids must be a list of element IDs (use list_views to get them)",
    };
}
var viewIds = new List<long>();
foreach (var v in viewIdsRaw)
    viewIds.Add(Convert.ToInt64(v));

var prefix = (string)inputs.GetValueOrDefault("sheet_prefix", "A-");
var start  = Convert.ToInt32(inputs.GetValueOrDefault("start_number", 101));

var titleBlockType = new FilteredElementCollector(doc)
    .OfCategory(BuiltInCategory.OST_TitleBlocks)
    .WhereElementIsElementType()
    .FirstOrDefault();

if (titleBlockType == null)
{
    return new Dictionary<string, object>
    {
        ["error"] = "No title block family loaded — load one first.",
    };
}

var created = new List<Dictionary<string, object>>();
var skipped = new List<Dictionary<string, object>>();

using (var t = new Transaction(doc, "Create sheet set"))
{
    t.Start();
    int n = start;
    foreach (var viewId in viewIds)
    {
        var view = doc.GetElement(new ElementId(viewId)) as View;
        if (view == null)
        {
            skipped.Add(new Dictionary<string, object>
            {
                ["view_id"] = viewId,
                ["reason"] = "view not found",
            });
            continue;
        }

        var sheet = ViewSheet.Create(doc, titleBlockType.Id);
        sheet.SheetNumber = prefix + n.ToString();
        sheet.Name = view.Name;

        try
        {
            Viewport.Create(doc, sheet.Id, view.Id, new XYZ(1.5, 1.0, 0));
        }
        catch (Exception ex)
        {
            skipped.Add(new Dictionary<string, object>
            {
                ["view_id"] = viewId,
                ["reason"] = "could not place: " + ex.Message,
            });
        }

        created.Add(new Dictionary<string, object>
        {
            ["sheet_id"] = (long)sheet.Id.Value,
            ["number"] = sheet.SheetNumber,
            ["name"] = sheet.Name,
        });
        n++;
    }
    t.Commit();
}

return new Dictionary<string, object>
{
    ["created_count"] = created.Count,
    ["skipped_count"] = skipped.Count,
    ["sheets"] = created,
    ["skipped"] = skipped,
};
