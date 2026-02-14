using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using RevitPipe = Autodesk.Revit.DB.Plumbing.Pipe;
using Autodesk.Revit.DB.Electrical;
using RevitOrchestrator.Execution;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.Commands;

/// <summary>
/// Creates a generic curve-based element (duct, pipe, conduit, cable tray, etc.)
/// between two points.
/// </summary>
public sealed class CreateElementCommand : IRevitCommand
{
    public string ToolName => "revit.create_element";
    public bool RequiresTransaction => true;

    public ToolResult Execute(Document doc, JsonElement args)
    {
        // Parse points
        var startArr = args.GetProperty("start_point");
        var endArr = args.GetProperty("end_point");

        var startPoint = new XYZ(
            startArr[0].GetDouble(),
            startArr[1].GetDouble(),
            startArr[2].GetDouble());
        var endPoint = new XYZ(
            endArr[0].GetDouble(),
            endArr[1].GetDouble(),
            endArr[2].GetDouble());

        // Find level
        Level? level = null;
        if (args.TryGetProperty("level_name", out var levelNameProp))
        {
            var levelName = levelNameProp.GetString();
            level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => l.Name == levelName);
        }
        level ??= new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .FirstOrDefault();

        if (level is null)
        {
            return ToolResult.Fail("", "REVIT_API_ERROR", "No levels found in the document");
        }

        // Get optional type name
        string? typeName = null;
        if (args.TryGetProperty("type_name", out var typeNameProp))
        {
            typeName = typeNameProp.GetString();
        }

        // Try to find a matching MEPCurveType by type_name
        var mepTypes = new FilteredElementCollector(doc)
            .OfClass(typeof(MEPCurveType))
            .Cast<MEPCurveType>()
            .ToList();

        MEPCurveType? matchedType = null;
        if (!string.IsNullOrEmpty(typeName))
        {
            matchedType = mepTypes.FirstOrDefault(t => t.Name == typeName);
        }

        // If no match by name, try to pick based on category hint
        if (matchedType is null && args.TryGetProperty("category", out var catProp))
        {
            var categoryHint = catProp.GetString()?.ToLowerInvariant() ?? "";
            matchedType = categoryHint switch
            {
                "ducts" or "duct" => mepTypes.OfType<DuctType>().FirstOrDefault(),
                "pipes" or "pipe" => mepTypes.OfType<PipeType>().FirstOrDefault(),
                "conduits" or "conduit" => mepTypes.OfType<ConduitType>().FirstOrDefault(),
                "cable trays" or "cabletray" => mepTypes.OfType<CableTrayType>().FirstOrDefault(),
                _ => null,
            };
        }

        // If still no match, fall back to the first available MEP type
        matchedType ??= mepTypes.FirstOrDefault();

        if (matchedType is null)
        {
            return ToolResult.Fail("", "REVIT_API_ERROR",
                "No MEP curve types found in the document. Ensure the project has duct, pipe, conduit, or cable tray types loaded.");
        }

        // Create the element based on matched type.
        // IMPORTANT: Use the (systemTypeId, curveTypeId, levelId, start, end) overload
        // to avoid ambiguity with the (curveTypeId, levelId, connector, start, end) overload
        // where passing null for connector causes the compiler to pick the wrong overload
        // and interpret null as the levelId.
        Element element;
        if (matchedType is DuctType ductType)
        {
            var systemType = new FilteredElementCollector(doc)
                .OfClass(typeof(MechanicalSystemType))
                .FirstOrDefault();
            if (systemType is null)
            {
                return ToolResult.Fail("", "REVIT_API_ERROR",
                    "No mechanical system types found in the document");
            }
            element = Duct.Create(doc, systemType.Id, ductType.Id, level.Id, startPoint, endPoint);
        }
        else if (matchedType is PipeType pipeType)
        {
            var systemType = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystemType))
                .FirstOrDefault();
            if (systemType is null)
            {
                return ToolResult.Fail("", "REVIT_API_ERROR",
                    "No piping system types found in the document");
            }
            element = RevitPipe.Create(doc, systemType.Id, pipeType.Id, level.Id, startPoint, endPoint);
        }
        else if (matchedType is ConduitType conduitType)
        {
            element = Conduit.Create(doc, conduitType.Id, startPoint, endPoint, level.Id);
        }
        else if (matchedType is CableTrayType cableTrayType)
        {
            element = CableTray.Create(doc, cableTrayType.Id, startPoint, endPoint, level.Id);
        }
        else
        {
            // Generic MEPCurve fallback — try duct creation as default
            var fallbackDuct = mepTypes.OfType<DuctType>().FirstOrDefault();
            var fallbackSystem = new FilteredElementCollector(doc)
                .OfClass(typeof(MechanicalSystemType))
                .FirstOrDefault();
            if (fallbackDuct != null && fallbackSystem != null)
            {
                element = Duct.Create(doc, fallbackSystem.Id, fallbackDuct.Id, level.Id, startPoint, endPoint);
            }
            else
            {
                return ToolResult.Fail("", "REVIT_API_ERROR",
                    $"Unsupported MEP curve type: {matchedType.GetType().Name}");
            }
        }

        // Apply optional diameter
        if (args.TryGetProperty("diameter", out var diamProp))
        {
            var diamParam = element.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                         ?? element.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
            diamParam?.Set(diamProp.GetDouble());
        }

        // Apply optional height/width for rectangular ducts
        if (args.TryGetProperty("height", out var heightProp))
        {
            var heightParam = element.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
            heightParam?.Set(heightProp.GetDouble());
        }
        if (args.TryGetProperty("width", out var widthProp))
        {
            var widthParam = element.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
            widthParam?.Set(widthProp.GetDouble());
        }

        return ToolResult.Ok("", new Dictionary<string, object?>
        {
            ["element_id"] = element.Id.Value,
            ["message"] = $"{matchedType.GetType().Name.Replace("Type", "")} created successfully",
        });
    }
}
