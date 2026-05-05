using System.Text.Json;

namespace RevitOrchestrator.Models;

/// <summary>
/// Request from Python to open the interactive Run Graph dialog. Sent when an
/// LLM tool call (dynamo.run_graph_interactive) wants the user to fill in
/// inputs before executing the graph.
/// </summary>
public sealed class DynamoRunDialogRequest
{
    /// <summary>Correlation id matching the original Python tool call.</summary>
    public string CallId { get; set; } = string.Empty;

    /// <summary>Absolute path to the .dyn file.</summary>
    public string GraphPath { get; set; } = string.Empty;

    /// <summary>
    /// Optional input values the LLM thinks should be used, keyed by input
    /// node Name. These pre-populate the form so the user can accept or edit.
    /// </summary>
    public JsonElement SuggestedInputs { get; set; }
}

/// <summary>
/// Result the dialog returns to Python after the user clicks Run, Cancel, or
/// closes the dialog.
/// </summary>
public sealed class DynamoRunDialogResult
{
    /// <summary>One of "ran", "cancelled", "error".</summary>
    public string Status { get; set; } = "cancelled";

    /// <summary>The values the user entered (for "ran").</summary>
    public Dictionary<string, object?>? InputsUsed { get; set; }

    /// <summary>The dynamo.run_graph result payload (for "ran").</summary>
    public Dictionary<string, object?>? Result { get; set; }

    /// <summary>Human-readable error string (for "error").</summary>
    public string Error { get; set; } = string.Empty;
}
