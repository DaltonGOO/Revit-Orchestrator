using System.Text.Json;

namespace RevitOrchestrator.Models;

/// <summary>
/// Represents a type-mapping request from the Python reconciliation mapper.
/// Shown when a recorded workflow references a type that doesn't exist in the
/// target model (e.g. different piping system type, missing family).
/// </summary>
public sealed class MappingRequest
{
    /// <summary>
    /// The unique call ID for correlating request/response.
    /// </summary>
    public string CallId { get; set; } = string.Empty;

    /// <summary>
    /// The kind of mapping: "system_type", "family_type", or "host_element".
    /// </summary>
    public string MappingType { get; set; } = string.Empty;

    /// <summary>
    /// The recorded value (name/identifier) from the original workflow.
    /// </summary>
    public string RecordedValue { get; set; } = string.Empty;

    /// <summary>
    /// Available options in the current model, each with at least "name" and "element_id".
    /// </summary>
    public JsonElement AvailableOptions { get; set; }

    /// <summary>
    /// The pre-selected best match, if any.
    /// </summary>
    public JsonElement BestMatch { get; set; }
}

/// <summary>
/// User's response to a mapping request.
/// </summary>
public sealed class MappingResponse
{
    /// <summary>
    /// The action taken: "map" (use selected), "create" (create new), or "cancel".
    /// </summary>
    public string Action { get; set; } = "cancel";

    /// <summary>
    /// The option the user selected (for "map" action).
    /// </summary>
    public Dictionary<string, object?>? SelectedOption { get; set; }
}
