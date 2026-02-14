using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RevitOrchestrator.Models;

/// <summary>
/// Represents a registered tool displayed in the Tools tab.
/// </summary>
public sealed class ToolInfo : INotifyPropertyChanged
{
    private bool _isFavorite;

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Adapter { get; set; } = string.Empty;
    public int ParameterCount { get; set; }

    /// <summary>
    /// Parameter definitions parsed from the tool's JSON schema (populated from tool_list_response).
    /// </summary>
    public Dictionary<string, PromotedParameter> ParameterSchema { get; set; } = new();

    /// <summary>Supported execution modes (e.g. "headless", "interactive").</summary>
    public List<string> ExecutionModes { get; set; } = new() { "headless" };

    /// <summary>Hint text describing what interactive mode does.</summary>
    public string? InteractiveHint { get; set; }

    /// <summary>Side effects declared by the tool.</summary>
    public List<string> SideEffects { get; set; } = new();

    /// <summary>Permission mode: "read" or "write".</summary>
    public string PermissionMode { get; set; } = "write";

    /// <summary>Whether user approval is required before execution.</summary>
    public bool ApprovalRequired { get; set; }

    /// <summary>Tool version string.</summary>
    public string? Version { get; set; }

    /// <summary>Tool author.</summary>
    public string? Author { get; set; }

    /// <summary>Tags for categorization.</summary>
    public List<string> Tags { get; set; } = new();

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
