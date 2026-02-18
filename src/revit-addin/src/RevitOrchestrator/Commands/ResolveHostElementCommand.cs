using System.Text.Json;
using Autodesk.Revit.DB;
using RevitOrchestrator.Execution;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.Commands;

/// <summary>
/// Finds a host element by unique_id or element_id. If not found, returns
/// nearby candidates of the same category. Read-only, used by the reconciliation mapper.
/// </summary>
public sealed class ResolveHostElementCommand : IRevitCommand
{
    public string ToolName => "revit.resolve_host";
    public bool RequiresTransaction => false;

    public ToolResult Execute(Document doc, JsonElement args)
    {
        Element? hostElement = null;
        string? requestedUniqueId = null;
        long? requestedElementId = null;
        string? hostCategory = null;

        if (args.TryGetProperty("host_unique_id", out var uidEl))
            requestedUniqueId = uidEl.GetString();
        if (args.TryGetProperty("host_element_id", out var eidEl))
            requestedElementId = eidEl.GetInt64();
        if (args.TryGetProperty("host_category", out var catEl))
            hostCategory = catEl.GetString();

        // 1. Try by unique_id (most reliable across models)
        if (!string.IsNullOrEmpty(requestedUniqueId))
        {
            hostElement = doc.GetElement(requestedUniqueId);
        }

        // 2. Try by element_id
        if (hostElement is null && requestedElementId.HasValue)
        {
#if REVIT2025 || REVIT2026
            hostElement = doc.GetElement(new ElementId(requestedElementId.Value));
#else
            hostElement = doc.GetElement(new ElementId((int)requestedElementId.Value));
#endif
        }

        if (hostElement != null)
        {
            return ToolResult.Ok("", new Dictionary<string, object?>
            {
                ["found"] = true,
                ["element_id"] = hostElement.Id.Value,
                ["unique_id"] = hostElement.UniqueId,
                ["name"] = hostElement.Name,
                ["category"] = hostElement.Category?.Name ?? "",
                ["candidates"] = new List<object>(),
            });
        }

        // Host not found — gather nearby candidates of the same category
        XYZ? location = null;
        if (args.TryGetProperty("location", out var locEl) && locEl.ValueKind == JsonValueKind.Array)
        {
            location = new XYZ(locEl[0].GetDouble(), locEl[1].GetDouble(), locEl[2].GetDouble());
        }

        var candidates = new List<Dictionary<string, object?>>();

        if (!string.IsNullOrEmpty(hostCategory))
        {
            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .ToList()
                .Where(e => e.Category?.Name == hostCategory)
                .ToList();

            // Sort by distance to the requested location if available
            if (location != null)
            {
                collector = collector
                    .Select(e => new { Element = e, Distance = GetDistanceTo(e, location) })
                    .Where(x => x.Distance < double.MaxValue)
                    .OrderBy(x => x.Distance)
                    .Take(10)
                    .Select(x => x.Element)
                    .ToList();
            }
            else
            {
                collector = collector.Take(10).ToList();
            }

            candidates = collector.Select(e => new Dictionary<string, object?>
            {
                ["element_id"] = e.Id.Value,
                ["unique_id"] = e.UniqueId,
                ["name"] = e.Name,
                ["category"] = e.Category?.Name ?? "",
            }).ToList();
        }

        return ToolResult.Ok("", new Dictionary<string, object?>
        {
            ["found"] = false,
            ["element_id"] = null,
            ["candidates"] = candidates,
            ["message"] = $"Host element not found. {candidates.Count} candidate(s) of category '{hostCategory}' available.",
        });
    }

    private static double GetDistanceTo(Element element, XYZ target)
    {
        try
        {
            if (element.Location is LocationPoint lp)
                return lp.Point.DistanceTo(target);
            if (element.Location is LocationCurve lc)
            {
                var midpoint = lc.Curve.Evaluate(0.5, true);
                return midpoint.DistanceTo(target);
            }
        }
        catch { }
        return double.MaxValue;
    }
}
