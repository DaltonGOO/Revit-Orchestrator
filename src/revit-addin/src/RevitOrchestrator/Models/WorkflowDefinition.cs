using RevitOrchestrator.Pipe;

namespace RevitOrchestrator.Models;

/// <summary>
/// Full workflow definition including steps, governance metadata, and LLM review results.
/// </summary>
public class WorkflowDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Tracks the original name when editing, so the server can delete the old file on rename.
    /// </summary>
    public string? OriginalName { get; set; }
    public List<WorkflowStep> Steps { get; set; } = new();

    /// <summary>
    /// Workflow-level input parameters promoted from step args.
    /// Keys are parameter names (e.g. "level_name"), values describe type/default.
    /// </summary>
    public Dictionary<string, PromotedParameter> Parameters { get; set; } = new();

    // Governance fields
    public string? Author { get; set; }
    public List<string> Tags { get; set; } = new();
    public string PermissionMode { get; set; } = "write";
    public bool ApprovalRequired { get; set; } = false;
    public List<string> SideEffects { get; set; } = new();
    public string Version { get; set; } = "1.0.0";

    // LLM review
    public string? LlmReviewSummary { get; set; }
    public List<string> LlmReviewFlags { get; set; } = new();
    public bool WasLlmReviewed { get; set; } = false;

    // Auto-derived from step tools (read-only, set from server response)
    public List<string> DerivedSideEffects { get; set; } = new();
    public string? DerivedPermissionMode { get; set; }
    public List<string> DerivedPreconditions { get; set; } = new();
}

/// <summary>
/// Describes a single promoted workflow parameter (type, description, default value).
/// </summary>
public class PromotedParameter
{
    public string Type { get; set; } = "string";
    public string Description { get; set; } = "";
    public object? DefaultValue { get; set; }
}
