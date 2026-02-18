using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitOrchestrator.Execution;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.Commands;

/// <summary>
/// Places a family instance in the active Revit document.
/// Routes to the correct Revit API overload based on hosting_behavior.
/// Never silently substitutes — fails with a clear error if family/type not found.
/// </summary>
public sealed class PlaceFamilyInstanceCommand : IRevitCommand
{
    public string ToolName => "revit.place_family";
    public bool RequiresTransaction => true;

    public ToolResult Execute(Document doc, JsonElement args)
    {
        // Required: family_name and family_type_name
        if (!args.TryGetProperty("family_name", out var familyNameEl))
            return ToolResult.Fail("", "MISSING_PARAMETER", "family_name is required");
        if (!args.TryGetProperty("family_type_name", out var typeNameEl))
            return ToolResult.Fail("", "MISSING_PARAMETER", "family_type_name is required");

        var familyName = familyNameEl.GetString() ?? "";
        var familyTypeName = typeNameEl.GetString() ?? "";

        // Find the FamilySymbol
        var symbol = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .FirstOrDefault(s => s.Family.Name == familyName && s.Name == familyTypeName);

        if (symbol is null)
        {
            // Try matching by type name only (family may have been renamed)
            symbol = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s => s.Name == familyTypeName && s.Family.Name == familyName);

            if (symbol is null)
            {
                return ToolResult.Fail("", "FAMILY_TYPE_NOT_FOUND",
                    $"Family type '{familyTypeName}' in family '{familyName}' not found in the document. " +
                    $"Use revit.resolve_family_type to find available types.");
            }
        }

        // Activate the symbol if not already active
        if (!symbol.IsActive)
        {
            symbol.Activate();
            doc.Regenerate();
        }

        // Parse location
        XYZ location;
        if (args.TryGetProperty("location", out var locEl) && locEl.ValueKind == JsonValueKind.Array)
        {
            location = new XYZ(locEl[0].GetDouble(), locEl[1].GetDouble(), locEl[2].GetDouble());
        }
        else
        {
            return ToolResult.Fail("", "MISSING_PARAMETER", "location [X, Y, Z] is required");
        }

        // Find level
        Level? level = null;
        if (args.TryGetProperty("level_name", out var levelProp))
        {
            var levelName = levelProp.GetString();
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
            return ToolResult.Fail("", "REVIT_API_ERROR", "No levels found in the document");

        // Parse hosting behavior
        var hostingBehavior = "PointBased";
        if (args.TryGetProperty("hosting_behavior", out var hostBehEl))
        {
            hostingBehavior = hostBehEl.GetString() ?? "PointBased";
        }

        // Parse structural usage
        var structuralType = StructuralType.NonStructural;
        if (args.TryGetProperty("structural_usage", out var structEl))
        {
            var structStr = structEl.GetString() ?? "";
            if (Enum.TryParse<StructuralType>(structStr, true, out var parsed))
            {
                structuralType = parsed;
            }
        }

        // Place the instance based on hosting behavior
        FamilyInstance instance;
        try
        {
            switch (hostingBehavior)
            {
                case "WallBased":
                case "Wall_based":
                    instance = PlaceWallBased(doc, symbol, location, level, structuralType, args);
                    break;

                case "FaceBased":
                case "Face_based":
                    instance = PlaceFaceBased(doc, symbol, location, level, structuralType, args);
                    break;

                case "WorkPlaneBased":
                case "Work_plane_based":
                    instance = PlaceWorkPlaneBased(doc, symbol, location, level, structuralType);
                    break;

                default: // PointBased, OneLevelBased, TwoLevelsBased, etc.
                    instance = doc.Create.NewFamilyInstance(location, symbol, level, structuralType);
                    break;
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Fail("", "REVIT_API_ERROR",
                $"Failed to place family instance: {ex.Message}");
        }

        // Apply rotation if specified
        if (args.TryGetProperty("rotation", out var rotEl) && rotEl.ValueKind == JsonValueKind.Number)
        {
            var rotation = rotEl.GetDouble();
            if (Math.Abs(rotation) > 1e-10)
            {
                var axis = Line.CreateBound(location, location + XYZ.BasisZ);
                ElementTransformUtils.RotateElement(doc, instance.Id, axis, rotation);
            }
        }

        return ToolResult.Ok("", new Dictionary<string, object?>
        {
            ["element_id"] = instance.Id.Value,
            ["message"] = $"Placed '{familyTypeName}' from family '{familyName}' successfully",
            ["hosting_behavior"] = hostingBehavior,
        });
    }

    private static FamilyInstance PlaceWallBased(
        Document doc, FamilySymbol symbol, XYZ location, Level level,
        StructuralType structuralType, JsonElement args)
    {
        Wall? hostWall = null;
        if (args.TryGetProperty("host_element_id", out var hostIdEl))
        {
            var hostId = hostIdEl.GetInt64();
#if REVIT2025 || REVIT2026
            var hostElement = doc.GetElement(new ElementId(hostId));
#else
            var hostElement = doc.GetElement(new ElementId((int)hostId));
#endif
            hostWall = hostElement as Wall;
        }

        if (hostWall is null)
        {
            throw new InvalidOperationException(
                "Wall-based placement requires a valid host_element_id pointing to a Wall. " +
                "Use revit.resolve_host to find a suitable host.");
        }

        return doc.Create.NewFamilyInstance(location, symbol, hostWall, level, structuralType);
    }

    private static FamilyInstance PlaceFaceBased(
        Document doc, FamilySymbol symbol, XYZ location, Level level,
        StructuralType structuralType, JsonElement args)
    {
        // For face-based, fall back to point-based placement with a level
        // (true face-based placement requires a Reference which can't be serialized).
        // This gives the closest approximation for replay scenarios.
        return doc.Create.NewFamilyInstance(location, symbol, level, structuralType);
    }

    private static FamilyInstance PlaceWorkPlaneBased(
        Document doc, FamilySymbol symbol, XYZ location, Level level,
        StructuralType structuralType)
    {
        // Place on level's work plane
        return doc.Create.NewFamilyInstance(location, symbol, level, structuralType);
    }
}
