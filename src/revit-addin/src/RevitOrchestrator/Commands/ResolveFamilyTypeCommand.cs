using System.Text.Json;
using Autodesk.Revit.DB;
using RevitOrchestrator.Execution;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.Commands;

/// <summary>
/// Lists available family symbols and finds an exact/family match for a recorded
/// family type. Read-only, used by the reconciliation mapper.
/// </summary>
public sealed class ResolveFamilyTypeCommand : IRevitCommand
{
    public string ToolName => "revit.resolve_family_type";
    public bool RequiresTransaction => false;

    public ToolResult Execute(Document doc, JsonElement args)
    {
        string? requestedFamily = null;
        string? requestedType = null;

        if (args.TryGetProperty("family_name", out var famEl))
            requestedFamily = famEl.GetString();
        if (args.TryGetProperty("family_type_name", out var typeEl))
            requestedType = typeEl.GetString();

        var allSymbols = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .ToList();

        // Filter to same family if family name is provided
        List<FamilySymbol> candidates;
        if (!string.IsNullOrEmpty(requestedFamily))
        {
            candidates = allSymbols
                .Where(s => s.Family.Name == requestedFamily)
                .ToList();

            // If no symbols in that family, show all symbols as candidates
            if (candidates.Count == 0)
                candidates = allSymbols;
        }
        else
        {
            candidates = allSymbols;
        }

        var available = candidates.Select(s => new Dictionary<string, object?>
        {
            ["family_name"] = s.Family.Name,
            ["type_name"] = s.Name,
            ["element_id"] = s.Id.Value,
            ["category"] = s.Category?.Name ?? "",
            ["placement_type"] = s.Family.FamilyPlacementType.ToString(),
        }).ToList();

        // Find best match
        Dictionary<string, object?>? bestMatch = null;
        bool exactMatch = false;

        // 1. Exact family + type match
        if (!string.IsNullOrEmpty(requestedFamily) && !string.IsNullOrEmpty(requestedType))
        {
            bestMatch = available.FirstOrDefault(a =>
                (string?)a["family_name"] == requestedFamily &&
                (string?)a["type_name"] == requestedType);
            exactMatch = bestMatch != null;
        }

        // 2. Type name match within any family
        if (bestMatch is null && !string.IsNullOrEmpty(requestedType))
        {
            bestMatch = available.FirstOrDefault(a =>
                (string?)a["type_name"] == requestedType);
        }

        // 3. First type in the same family
        if (bestMatch is null && !string.IsNullOrEmpty(requestedFamily))
        {
            bestMatch = available.FirstOrDefault(a =>
                (string?)a["family_name"] == requestedFamily);
        }

        return ToolResult.Ok("", new Dictionary<string, object?>
        {
            ["available"] = available,
            ["best_match"] = bestMatch,
            ["exact_match"] = exactMatch,
        });
    }
}
