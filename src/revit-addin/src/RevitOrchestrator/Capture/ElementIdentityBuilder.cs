using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace RevitOrchestrator.Capture;

/// <summary>
/// Builds rich identity data for a Revit element, including category-specific
/// key parameters, bounding box, and connector information.
/// </summary>
public static class ElementIdentityBuilder
{
    /// <summary>
    /// Build identity data for an element.
    /// </summary>
    public static ElementIdentity Build(Document doc, Element element)
    {
        var identity = new ElementIdentity
        {
            UniqueId = element.UniqueId,
            ElementId = element.Id.Value,
            Category = element.Category?.Name ?? "",
        };

        // Type info
        var typeId = element.GetTypeId();
        if (typeId != ElementId.InvalidElementId)
        {
            var elementType = doc.GetElement(typeId) as ElementType;
            identity.TypeName = elementType?.Name ?? "";

            if (elementType is FamilySymbol fs)
            {
                identity.FamilyName = fs.FamilyName;
            }
        }

        // Bounding box
        var bbox = element.get_BoundingBox(null);
        if (bbox != null)
        {
            identity.BboxMin = new[] { bbox.Min.X, bbox.Min.Y, bbox.Min.Z };
            identity.BboxMax = new[] { bbox.Max.X, bbox.Max.Y, bbox.Max.Z };
        }

        // Category-specific key parameters
        identity.KeyParams = ExtractKeyParams(element);

        // Connector info
        identity.ConnectorInfo = ExtractConnectorInfo(element);

        return identity;
    }

    private static Dictionary<string, object?> ExtractKeyParams(Element element)
    {
        var keyParams = new Dictionary<string, object?>();
        var category = element.Category?.Name ?? "";

        switch (category)
        {
            case "Walls":
                TryAddParam(element, BuiltInParameter.WALL_USER_HEIGHT_PARAM, "height", keyParams);
                TryAddParam(element, BuiltInParameter.WALL_BASE_OFFSET, "base_offset", keyParams);
                TryAddParam(element, BuiltInParameter.WALL_ATTR_WIDTH_PARAM, "width", keyParams);
                break;

            case "Ducts":
                TryAddParam(element, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, "diameter", keyParams);
                TryAddParam(element, BuiltInParameter.RBS_CURVE_WIDTH_PARAM, "width", keyParams);
                TryAddParam(element, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, "height", keyParams);
                TryAddParam(element, BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM, "system_type", keyParams);
                break;

            case "Pipes":
                TryAddParam(element, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, "diameter", keyParams);
                TryAddParam(element, BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM, "system_type", keyParams);
                break;

            case "Floors":
                TryAddParam(element, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM, "offset", keyParams);
                break;
        }

        // Always try to get level
        TryAddParam(element, BuiltInParameter.FAMILY_LEVEL_PARAM, "level_id", keyParams);

        return keyParams;
    }

    private static List<Dictionary<string, object?>> ExtractConnectorInfo(Element element)
    {
        var connectors = new List<Dictionary<string, object?>>();

        ConnectorSet? connectorSet = null;
        if (element is FamilyInstance fi)
        {
            connectorSet = fi.MEPModel?.ConnectorManager?.Connectors;
        }
        else if (element is MEPCurve mepCurve)
        {
            connectorSet = mepCurve.ConnectorManager?.Connectors;
        }

        if (connectorSet == null) return connectors;

        foreach (Connector connector in connectorSet)
        {
            var info = new Dictionary<string, object?>
            {
                ["domain"] = connector.Domain.ToString(),
                ["type"] = connector.ConnectorType.ToString(),
            };

            try
            {
                var origin = connector.Origin;
                info["origin"] = new[] { origin.X, origin.Y, origin.Z };
            }
            catch { }

            try
            {
                info["shape"] = connector.Shape.ToString();
            }
            catch { }

            connectors.Add(info);
        }

        return connectors;
    }

    private static void TryAddParam(
        Element element,
        BuiltInParameter bip,
        string key,
        Dictionary<string, object?> dict)
    {
        var param = element.get_Parameter(bip);
        if (param == null || !param.HasValue) return;

        object? value = param.StorageType switch
        {
            StorageType.Integer => param.AsInteger(),
            StorageType.Double => param.AsDouble(),
            StorageType.String => param.AsString(),
            StorageType.ElementId => param.AsElementId()?.Value,
            _ => null,
        };

        if (value != null)
            dict[key] = value;
    }
}

/// <summary>
/// Rich identity data for a single Revit element.
/// </summary>
public sealed class ElementIdentity
{
    public string UniqueId { get; set; } = string.Empty;
    public long ElementId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public double[]? BboxMin { get; set; }
    public double[]? BboxMax { get; set; }
    public Dictionary<string, object?> KeyParams { get; set; } = new();
    public List<Dictionary<string, object?>> ConnectorInfo { get; set; } = new();

    public Dictionary<string, object?> ToDictionary()
    {
        var dict = new Dictionary<string, object?>
        {
            ["unique_id"] = UniqueId,
            ["element_id"] = ElementId,
            ["category"] = Category,
            ["family_name"] = FamilyName,
            ["type_name"] = TypeName,
        };

        if (BboxMin != null)
            dict["bbox_min"] = BboxMin;
        if (BboxMax != null)
            dict["bbox_max"] = BboxMax;
        if (KeyParams.Count > 0)
            dict["key_params"] = KeyParams;
        if (ConnectorInfo.Count > 0)
            dict["connector_info"] = ConnectorInfo;

        return dict;
    }
}
