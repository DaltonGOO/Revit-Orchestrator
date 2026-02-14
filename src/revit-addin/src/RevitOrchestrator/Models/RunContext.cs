namespace RevitOrchestrator.Models;

/// <summary>
/// Represents the current Revit runtime context for precondition checks.
/// </summary>
public sealed class RunContext
{
    /// <summary>
    /// The Revit application version (e.g., "2025").
    /// </summary>
    public string RevitVersion { get; set; } = string.Empty;

    /// <summary>
    /// Title of the currently open document, or empty if no document is open.
    /// </summary>
    public string DocumentTitle { get; set; } = string.Empty;

    /// <summary>
    /// GUID of the currently open document.
    /// </summary>
    public string DocumentGuid { get; set; } = string.Empty;

    /// <summary>
    /// Whether worksharing is enabled on the current document.
    /// </summary>
    public bool IsWorksharingEnabled { get; set; }

    /// <summary>
    /// The type of the currently active view (e.g., "FloorPlan", "ThreeD").
    /// </summary>
    public string ActiveViewType { get; set; } = string.Empty;

    /// <summary>
    /// The current Revit user name.
    /// </summary>
    public string UserName { get; set; } = string.Empty;
}
