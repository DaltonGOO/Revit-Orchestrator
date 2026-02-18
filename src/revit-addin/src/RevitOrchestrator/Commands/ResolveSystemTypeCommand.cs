using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using RevitOrchestrator.Execution;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.Commands;

/// <summary>
/// Lists available piping/mechanical system types and finds the best match
/// for a recorded system type name. Read-only, used by the reconciliation mapper.
/// </summary>
public sealed class ResolveSystemTypeCommand : IRevitCommand
{
    public string ToolName => "revit.resolve_system_type";
    public bool RequiresTransaction => false;

    public ToolResult Execute(Document doc, JsonElement args)
    {
        // What kind of system type are we looking for?
        var kind = "piping"; // default
        if (args.TryGetProperty("kind", out var kindEl))
            kind = kindEl.GetString()?.ToLowerInvariant() ?? "piping";

        string? requestedName = null;
        if (args.TryGetProperty("system_type_name", out var nameEl))
            requestedName = nameEl.GetString();

        string? requestedClassification = null;
        if (args.TryGetProperty("mep_system_classification", out var classEl))
            requestedClassification = classEl.GetString();

        List<Dictionary<string, object?>> available;
        Dictionary<string, object?>? bestMatch = null;

        if (kind == "mechanical")
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(MechanicalSystemType))
                .Cast<MechanicalSystemType>()
                .ToList();

            available = types.Select(t => new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["element_id"] = t.Id.Value,
                ["classification"] = t.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM)?.AsString() ?? "",
            }).ToList();

            bestMatch = FindBestMatch(available, requestedName, requestedClassification);
        }
        else
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .ToList();

            available = types.Select(t => new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["element_id"] = t.Id.Value,
                ["classification"] = t.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM)?.AsString() ?? "",
            }).ToList();

            bestMatch = FindBestMatch(available, requestedName, requestedClassification);
        }

        return ToolResult.Ok("", new Dictionary<string, object?>
        {
            ["available"] = available,
            ["best_match"] = bestMatch,
            ["exact_match"] = bestMatch != null && (string?)(bestMatch["name"]) == requestedName,
            ["kind"] = kind,
        });
    }

    private static Dictionary<string, object?>? FindBestMatch(
        List<Dictionary<string, object?>> available,
        string? requestedName,
        string? requestedClassification)
    {
        if (available.Count == 0) return null;

        // 1. Exact name match
        if (!string.IsNullOrEmpty(requestedName))
        {
            var exact = available.FirstOrDefault(a => (string?)a["name"] == requestedName);
            if (exact != null) return exact;
        }

        // 2. Classification match
        if (!string.IsNullOrEmpty(requestedClassification))
        {
            var byClass = available.FirstOrDefault(a => (string?)a["classification"] == requestedClassification);
            if (byClass != null) return byClass;
        }

        // 3. No match found
        return null;
    }
}
