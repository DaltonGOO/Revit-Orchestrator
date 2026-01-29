using System.Text.Json;
using Autodesk.Revit.DB;
using RevitOrchestrator.Execution;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.Commands;

/// <summary>
/// Creates a wall between two points with a specified height.
/// </summary>
public sealed class CreateWallCommand : IRevitCommand
{
    public string ToolName => "revit.create_wall";
    public bool RequiresTransaction => true;

    public ToolResult Execute(Document doc, JsonElement args)
    {
        // Parse points
        var startArr = args.GetProperty("start_point");
        var endArr = args.GetProperty("end_point");
        var height = args.GetProperty("height").GetDouble();

        var startPoint = new XYZ(
            startArr[0].GetDouble(),
            startArr[1].GetDouble(),
            startArr[2].GetDouble());
        var endPoint = new XYZ(
            endArr[0].GetDouble(),
            endArr[1].GetDouble(),
            endArr[2].GetDouble());

        // Create line
        var line = Line.CreateBound(startPoint, endPoint);

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

        // Find wall type
        WallType? wallType = null;
        if (args.TryGetProperty("wall_type", out var wallTypeProp))
        {
            var wallTypeName = wallTypeProp.GetString();
            wallType = new FilteredElementCollector(doc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .FirstOrDefault(wt => wt.Name == wallTypeName);

            if (wallType is null)
            {
                return ToolResult.Fail("", "REVIT_API_ERROR",
                    $"Wall type '{wallTypeName}' not found");
            }
        }

        // Create wall
        Wall wall;
        if (wallType is not null)
        {
#if REVIT2025 || REVIT2026
            wall = Wall.Create(doc, line, wallType.Id, level.Id, height, 0, false, false);
#else
            wall = Wall.Create(doc, line, wallType.Id, level.Id, height, 0, false, false);
#endif
        }
        else
        {
            wall = Wall.Create(doc, line, level.Id, false);
            wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.Set(height);
        }

        return ToolResult.Ok("", new Dictionary<string, object?>
        {
            ["element_id"] = wall.Id.Value,
            ["message"] = "Wall created successfully",
        });
    }
}
