using System.Collections.Generic;

namespace RevitOrchestrator.Models;

/// <summary>
/// Tracks changes made to the Revit model during a tool execution.
/// </summary>
public sealed class ModelChanges
{
    /// <summary>
    /// Elements that were created during the operation.
    /// </summary>
    public List<ElementChange> Created { get; set; } = new();

    /// <summary>
    /// Elements that were modified during the operation.
    /// </summary>
    public List<ElementChange> Modified { get; set; } = new();

    /// <summary>
    /// Element IDs that were deleted during the operation.
    /// </summary>
    public List<long> Deleted { get; set; } = new();

    /// <summary>
    /// Whether any changes were made.
    /// </summary>
    public bool HasChanges => Created.Count > 0 || Modified.Count > 0 || Deleted.Count > 0;

    /// <summary>
    /// Converts to a dictionary for JSON serialization.
    /// </summary>
    public Dictionary<string, object?> ToDictionary()
    {
        return new Dictionary<string, object?>
        {
            ["created"] = Created.ConvertAll(e => e.ToDictionary()),
            ["modified"] = Modified.ConvertAll(e => e.ToDictionary()),
            ["deleted"] = Deleted,
        };
    }
}

/// <summary>
/// Information about a created or modified element.
/// </summary>
public sealed class ElementChange
{
    /// <summary>
    /// The element's unique ID in Revit.
    /// </summary>
    public long ElementId { get; set; }

    /// <summary>
    /// The element's persistent UniqueId for cross-session identity.
    /// </summary>
    public string? UniqueId { get; set; }

    /// <summary>
    /// The element's category name (e.g., "Walls", "Floors").
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// The element's type name.
    /// </summary>
    public string? TypeName { get; set; }

    /// <summary>
    /// The element's family name (e.g., "Basic Wall", "Round Duct").
    /// </summary>
    public string? FamilyName { get; set; }

    /// <summary>
    /// The element's instance name if applicable.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Bounding box minimum point [X, Y, Z].
    /// </summary>
    public double[]? BboxMin { get; set; }

    /// <summary>
    /// Bounding box maximum point [X, Y, Z].
    /// </summary>
    public double[]? BboxMax { get; set; }

    /// <summary>
    /// For modified elements, the list of parameter changes.
    /// </summary>
    public List<ParameterChange>? ParameterChanges { get; set; }

    /// <summary>
    /// Converts to a dictionary for JSON serialization.
    /// </summary>
    public Dictionary<string, object?> ToDictionary()
    {
        var dict = new Dictionary<string, object?>
        {
            ["element_id"] = ElementId,
            ["unique_id"] = UniqueId,
            ["category"] = Category,
            ["type_name"] = TypeName,
        };

        if (!string.IsNullOrEmpty(FamilyName))
            dict["family_name"] = FamilyName;

        if (!string.IsNullOrEmpty(Name))
            dict["name"] = Name;

        if (BboxMin != null)
            dict["bbox_min"] = BboxMin;
        if (BboxMax != null)
            dict["bbox_max"] = BboxMax;

        if (ParameterChanges is { Count: > 0 })
            dict["parameter_changes"] = ParameterChanges.ConvertAll(p => p.ToDictionary());

        return dict;
    }
}

/// <summary>
/// Represents a change to a single parameter value.
/// </summary>
public sealed class ParameterChange
{
    public string ParameterName { get; set; } = string.Empty;
    public object? OldValue { get; set; }
    public object? NewValue { get; set; }

    public Dictionary<string, object?> ToDictionary()
    {
        return new Dictionary<string, object?>
        {
            ["parameter_name"] = ParameterName,
            ["old_value"] = OldValue,
            ["new_value"] = NewValue,
        };
    }
}
