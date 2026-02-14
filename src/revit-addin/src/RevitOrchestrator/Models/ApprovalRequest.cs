using System.Text.Json;

namespace RevitOrchestrator.Models;

/// <summary>
/// Represents an approval request from the Python server for a tool execution.
/// </summary>
public sealed class ApprovalRequest
{
    /// <summary>
    /// The unique call ID for correlating request/response.
    /// </summary>
    public string CallId { get; set; } = string.Empty;

    /// <summary>
    /// The name of the tool requesting approval.
    /// </summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>
    /// The arguments that will be passed to the tool.
    /// </summary>
    public JsonElement Args { get; set; }

    /// <summary>
    /// The side effects this tool may cause.
    /// </summary>
    public string[] SideEffects { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Human-readable description of what the tool will do.
    /// </summary>
    public string Description { get; set; } = string.Empty;
}
