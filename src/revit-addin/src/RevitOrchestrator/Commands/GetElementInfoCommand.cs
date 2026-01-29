using System.Text.Json;
using Autodesk.Revit.DB;
using RevitOrchestrator.Execution;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.Commands;

/// <summary>
/// Retrieves detailed information about a Revit element.
/// </summary>
public sealed class GetElementInfoCommand : IRevitCommand
{
    public string ToolName => "revit.get_element_info";
    public bool RequiresTransaction => false;

    public ToolResult Execute(Document doc, JsonElement args)
    {
        var elementId = args.GetProperty("element_id").GetInt64();
        var includeParameters = true;
        var includeGeometry = false;

        if (args.TryGetProperty("include_parameters", out var ip))
            includeParameters = ip.GetBoolean();
        if (args.TryGetProperty("include_geometry", out var ig))
            includeGeometry = ig.GetBoolean();

#if REVIT2025 || REVIT2026
        var element = doc.GetElement(new ElementId((long)elementId));
#else
        var element = doc.GetElement(new ElementId((int)elementId));
#endif

        if (element is null)
        {
            return ToolResult.Fail("", "REVIT_API_ERROR",
                $"Element with ID {elementId} not found");
        }

        var data = new Dictionary<string, object?>
        {
            ["element_id"] = elementId,
            ["category"] = element.Category?.Name,
            ["type_name"] = element.GetType().Name,
            ["name"] = element.Name,
        };

        // Level
        if (element.LevelId != ElementId.InvalidElementId)
        {
            var level = doc.GetElement(element.LevelId);
            data["level"] = level?.Name;
        }

        // Parameters
        if (includeParameters)
        {
            var parameters = new Dictionary<string, object?>();
            foreach (Parameter param in element.Parameters)
            {
                if (!param.HasValue) continue;
                var value = param.StorageType switch
                {
                    StorageType.String => (object?)param.AsString(),
                    StorageType.Integer => param.AsInteger(),
                    StorageType.Double => param.AsDouble(),
                    StorageType.ElementId => param.AsElementId().Value,
                    _ => null,
                };
                parameters[param.Definition.Name] = value;
            }
            data["parameters"] = parameters;
        }

        // Bounding box
        if (includeGeometry)
        {
            var bb = element.get_BoundingBox(null);
            if (bb is not null)
            {
                data["bounding_box"] = new Dictionary<string, object?>
                {
                    ["min"] = new[] { bb.Min.X, bb.Min.Y, bb.Min.Z },
                    ["max"] = new[] { bb.Max.X, bb.Max.Y, bb.Max.Z },
                };
            }
        }

        return ToolResult.Ok("", data);
    }
}
