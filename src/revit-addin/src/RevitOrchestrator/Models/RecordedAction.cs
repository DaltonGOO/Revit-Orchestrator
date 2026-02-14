using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RevitOrchestrator.Models;

/// <summary>
/// Represents a single recorded user action in Revit.
/// </summary>
public sealed class RecordedAction : INotifyPropertyChanged
{
    private bool _isSelected;

    /// <summary>
    /// Unique identifier for this action.
    /// </summary>
    public string ActionId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// When the action occurred.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>
    /// The type of action performed.
    /// </summary>
    public RecordedActionType ActionType { get; set; }

    /// <summary>
    /// The Revit command name if known (e.g., "ID_WALL", "ID_FLOOR").
    /// </summary>
    public string? CommandName { get; set; }

    /// <summary>
    /// Human-readable description of the action.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Category of elements affected (e.g., "Walls", "Floors").
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Element IDs that were created by this action.
    /// </summary>
    public List<long> CreatedElementIds { get; set; } = new();

    /// <summary>
    /// Element IDs that were modified by this action.
    /// </summary>
    public List<long> ModifiedElementIds { get; set; } = new();

    /// <summary>
    /// Element IDs that were deleted by this action.
    /// </summary>
    public List<long> DeletedElementIds { get; set; } = new();

    /// <summary>
    /// Additional parameters captured for this action.
    /// </summary>
    public Dictionary<string, object?> Parameters { get; set; } = new();

    /// <summary>
    /// The tool name this action maps to (for workflow creation).
    /// </summary>
    public string? MappedToolName { get; set; }

    /// <summary>
    /// Whether this action is selected for workflow creation.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Formatted timestamp for display.
    /// </summary>
    public string TimestampText => Timestamp.ToString("HH:mm:ss");

    /// <summary>
    /// Icon for the action type.
    /// </summary>
    public string ActionIcon => ActionType switch
    {
        RecordedActionType.Create => "+",
        RecordedActionType.Modify => "~",
        RecordedActionType.Delete => "-",
        RecordedActionType.Command => "\u25B6",
        _ => "\u25CF"
    };

    /// <summary>
    /// Color for the action icon.
    /// </summary>
    public string ActionColor => ActionType switch
    {
        RecordedActionType.Create => "#4CAF50",
        RecordedActionType.Modify => "#FF9800",
        RecordedActionType.Delete => "#F44336",
        RecordedActionType.Command => "#2196F3",
        _ => "#9E9E9E"
    };

    /// <summary>
    /// Summary text showing element counts.
    /// </summary>
    public string ElementSummary
    {
        get
        {
            var parts = new List<string>();
            if (CreatedElementIds.Count > 0)
                parts.Add($"+{CreatedElementIds.Count}");
            if (ModifiedElementIds.Count > 0)
                parts.Add($"~{ModifiedElementIds.Count}");
            if (DeletedElementIds.Count > 0)
                parts.Add($"-{DeletedElementIds.Count}");
            return parts.Count > 0 ? string.Join(" ", parts) : "";
        }
    }

    /// <summary>
    /// Creates a signature for pattern matching (action type + category).
    /// </summary>
    public string GetSignature()
    {
        var parts = new List<string> { ActionType.ToString() };
        if (!string.IsNullOrEmpty(Category))
            parts.Add(Category);
        if (!string.IsNullOrEmpty(CommandName))
            parts.Add(CommandName);
        return string.Join(":", parts);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Types of recorded actions.
/// </summary>
public enum RecordedActionType
{
    /// <summary>Elements were created.</summary>
    Create,

    /// <summary>Elements were modified.</summary>
    Modify,

    /// <summary>Elements were deleted.</summary>
    Delete,

    /// <summary>A Revit command was executed.</summary>
    Command,

    /// <summary>Unknown or mixed action.</summary>
    Unknown
}
