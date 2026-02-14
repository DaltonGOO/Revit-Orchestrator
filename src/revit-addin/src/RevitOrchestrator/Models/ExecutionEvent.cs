using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace RevitOrchestrator.Models;

/// <summary>
/// Represents a tool execution event displayed in the History tab.
/// </summary>
public sealed class ExecutionEvent : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isExpanded;
    private string _eventType = string.Empty;
    private string? _resultJson;

    /// <summary>
    /// Unique identifier for this execution event.
    /// </summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>
    /// Type of event: "started", "completed", or "failed".
    /// </summary>
    public string EventType
    {
        get => _eventType;
        set { _eventType = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusIcon)); OnPropertyChanged(nameof(StatusColor)); }
    }

    /// <summary>
    /// Name of the tool being executed.
    /// </summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>
    /// JSON string of the tool arguments.
    /// </summary>
    public string ArgsJson { get; set; } = string.Empty;

    /// <summary>
    /// Groups related calls within a single agentic loop.
    /// </summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// Order within the correlation group.
    /// </summary>
    public int Sequence { get; set; }

    /// <summary>
    /// When the event occurred.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>
    /// JSON string of the tool result (for completed events).
    /// </summary>
    public string? ResultJson
    {
        get => _resultJson;
        set
        {
            _resultJson = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasResult));
            OnPropertyChanged(nameof(ResultSummary));
            OnPropertyChanged(nameof(ModelChangesSummary));
            OnPropertyChanged(nameof(HasModelChanges));
        }
    }

    /// <summary>
    /// Execution duration in milliseconds.
    /// </summary>
    public int DurationMs { get; set; }

    /// <summary>
    /// Error message (for failed events).
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Origin of the execution: "chat", "manual", or "test".
    /// </summary>
    public string Origin { get; set; } = "chat";

    /// <summary>
    /// Human-readable label for the origin.
    /// </summary>
    public string OriginLabel => Origin switch
    {
        "manual" => "Manual",
        "test" => "Test",
        _ => "Chat"
    };

    /// <summary>
    /// Color for the origin badge.
    /// </summary>
    public string OriginColor => Origin switch
    {
        "manual" => "#FF8F00",
        "test" => "#9C27B0",
        _ => "#1a73e8"
    };

    /// <summary>
    /// Whether this event is selected for workflow saving.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Whether the details panel is expanded.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Whether this event has a result.
    /// </summary>
    public bool HasResult => !string.IsNullOrEmpty(ResultJson);

    /// <summary>
    /// Summary of the result for display.
    /// </summary>
    public string ResultSummary
    {
        get
        {
            if (string.IsNullOrEmpty(ResultJson)) return "";
            try
            {
                using var doc = JsonDocument.Parse(ResultJson);
                var root = doc.RootElement;

                // Try to get message first
                if (root.TryGetProperty("data", out var data))
                {
                    if (data.TryGetProperty("message", out var msg))
                        return msg.GetString() ?? "";
                }
                if (root.TryGetProperty("message", out var message))
                    return message.GetString() ?? "";

                return "Completed";
            }
            catch
            {
                return "Completed";
            }
        }
    }

    /// <summary>
    /// Whether this event has model changes to display.
    /// </summary>
    public bool HasModelChanges
    {
        get
        {
            if (string.IsNullOrEmpty(ResultJson)) return false;
            try
            {
                using var doc = JsonDocument.Parse(ResultJson);
                var root = doc.RootElement;

                // Check for model_changes in data
                if (root.TryGetProperty("data", out var data))
                {
                    if (data.TryGetProperty("model_changes", out _)) return true;
                    // Also check for element_id which indicates a creation
                    if (data.TryGetProperty("element_id", out _)) return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Human-readable summary of model changes.
    /// </summary>
    public string ModelChangesSummary
    {
        get
        {
            if (string.IsNullOrEmpty(ResultJson)) return "";
            try
            {
                using var doc = JsonDocument.Parse(ResultJson);
                var root = doc.RootElement;
                var lines = new List<string>();

                if (root.TryGetProperty("data", out var data))
                {
                    // Check for structured model_changes
                    if (data.TryGetProperty("model_changes", out var changes))
                    {
                        if (changes.TryGetProperty("created", out var created) && created.GetArrayLength() > 0)
                        {
                            foreach (var elem in created.EnumerateArray())
                            {
                                var id = elem.TryGetProperty("element_id", out var eid) ? eid.ToString() : "?";
                                var cat = elem.TryGetProperty("category", out var c) ? c.GetString() : "";
                                var type = elem.TryGetProperty("type_name", out var t) ? t.GetString() : "";
                                lines.Add($"+ Created {cat} (ID: {id}) - {type}");
                            }
                        }
                        if (changes.TryGetProperty("modified", out var modified) && modified.GetArrayLength() > 0)
                        {
                            foreach (var elem in modified.EnumerateArray())
                            {
                                var id = elem.TryGetProperty("element_id", out var eid) ? eid.ToString() : "?";
                                lines.Add($"~ Modified element ID: {id}");
                            }
                        }
                        if (changes.TryGetProperty("deleted", out var deleted) && deleted.GetArrayLength() > 0)
                        {
                            foreach (var elem in deleted.EnumerateArray())
                            {
                                var id = elem.TryGetProperty("element_id", out var eid) ? eid.ToString() : "?";
                                lines.Add($"- Deleted element ID: {id}");
                            }
                        }
                    }
                    // Fallback: check for simple element_id (legacy format)
                    else if (data.TryGetProperty("element_id", out var elementId))
                    {
                        lines.Add($"+ Created element ID: {elementId}");
                    }
                }

                return lines.Count > 0 ? string.Join("\n", lines) : "";
            }
            catch
            {
                return "";
            }
        }
    }

    /// <summary>
    /// Icon to display based on event type.
    /// </summary>
    public string StatusIcon => EventType switch
    {
        "started" => "\u25B6", // Play symbol
        "completed" => "\u2713", // Check mark
        "failed" => "\u2717", // X mark
        _ => "\u25CF" // Bullet
    };

    /// <summary>
    /// Color for the status indicator.
    /// </summary>
    public string StatusColor => EventType switch
    {
        "started" => "#FFA500", // Orange
        "completed" => "#4CAF50", // Green
        "failed" => "#F44336", // Red
        _ => "#9E9E9E" // Gray
    };

    /// <summary>
    /// Formatted duration string.
    /// </summary>
    public string DurationText => DurationMs > 0 ? $"{DurationMs}ms" : "";

    /// <summary>
    /// Formatted timestamp string.
    /// </summary>
    public string TimestampText => Timestamp.ToString("HH:mm:ss.fff");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
