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
            OnPropertyChanged(nameof(HasDynamoWarnings));
            OnPropertyChanged(nameof(DynamoWarningsSummary));
            OnPropertyChanged(nameof(GraphPath));
            OnPropertyChanged(nameof(StatusIcon));
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(ResultColor));
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
    /// Whether this event has an error message.
    /// </summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>
    /// Short summary of the error (first line only) for inline display.
    /// The full error is shown in the expanded details section.
    /// </summary>
    public string? ErrorSummary
    {
        get
        {
            if (string.IsNullOrEmpty(ErrorMessage)) return null;
            var firstLine = ErrorMessage.Split('\n')[0].Trim();
            return firstLine.Length > 120 ? firstLine[..117] + "..." : firstLine;
        }
    }

    /// <summary>
    /// The primary error text (everything before the Diagnostics separator).
    /// </summary>
    public string? ErrorPrimary
    {
        get
        {
            if (string.IsNullOrEmpty(ErrorMessage)) return null;
            var idx = ErrorMessage.IndexOf("\n\nDiagnostics:", StringComparison.Ordinal);
            return idx >= 0 ? ErrorMessage[..idx].Trim() : ErrorMessage;
        }
    }

    /// <summary>
    /// Diagnostic details (everything after the Diagnostics separator), or null.
    /// </summary>
    public string? DiagnosticsText
    {
        get
        {
            if (string.IsNullOrEmpty(ErrorMessage)) return null;
            var idx = ErrorMessage.IndexOf("\n\nDiagnostics:", StringComparison.Ordinal);
            if (idx < 0) return null;
            return ErrorMessage[(idx + 2)..].Trim(); // skip the \n\n
        }
    }

    /// <summary>
    /// Whether this event has diagnostic details.
    /// </summary>
    public bool HasDiagnostics => DiagnosticsText != null;

    /// <summary>
    /// Full run details as copyable text (tool name, args, error/result, duration).
    /// </summary>
    public string RunDetailsText
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Tool: {ToolName}");
            sb.AppendLine($"Time: {TimestampText}");
            sb.AppendLine($"Status: {EventType}");
            if (DurationMs > 0) sb.AppendLine($"Duration: {DurationMs}ms");
            sb.AppendLine($"Input Args: {ArgsJson}");
            if (!string.IsNullOrEmpty(ErrorMessage))
                sb.AppendLine($"Error: {ErrorMessage}");
            if (!string.IsNullOrEmpty(ResultJson))
                sb.AppendLine($"Result: {ResultJson}");
            return sb.ToString().TrimEnd();
        }
    }

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

                string summary = "Completed";

                // Try to get message first
                if (root.TryGetProperty("data", out var data))
                {
                    if (data.TryGetProperty("message", out var msg))
                        summary = msg.GetString() ?? "Completed";
                }
                else if (root.TryGetProperty("message", out var message))
                {
                    summary = message.GetString() ?? "Completed";
                }

                // Append warning count for Dynamo results
                if (HasDynamoWarnings)
                {
                    int warnCount = 0, errCount = 0;
                    if (root.TryGetProperty("data", out var d))
                    {
                        if (d.TryGetProperty("warnings", out var w)) warnCount = w.GetArrayLength();
                        if (d.TryGetProperty("errors", out var e)) errCount = e.GetArrayLength();
                    }
                    var parts = new List<string>();
                    if (errCount > 0) parts.Add($"{errCount} error(s)");
                    if (warnCount > 0) parts.Add($"{warnCount} warning(s)");
                    if (parts.Count > 0)
                        summary += $" — {string.Join(", ", parts)}";
                }

                return summary;
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
    /// Whether this is a Dynamo tool execution.
    /// </summary>
    public bool IsDynamoTool => ToolName.StartsWith("dynamo.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The graph_path from a Dynamo execution result, or null.
    /// </summary>
    public string? GraphPath
    {
        get
        {
            if (!IsDynamoTool) return null;
            // Try result data first
            if (!string.IsNullOrEmpty(ResultJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(ResultJson);
                    if (doc.RootElement.TryGetProperty("data", out var data)
                        && data.TryGetProperty("graph_path", out var gp))
                        return gp.GetString();
                }
                catch { }
            }
            // Fallback to args
            if (!string.IsNullOrEmpty(ArgsJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(ArgsJson);
                    if (doc.RootElement.TryGetProperty("graph_path", out var gp))
                        return gp.GetString();
                }
                catch { }
            }
            return null;
        }
    }

    /// <summary>
    /// Whether this Dynamo execution had warnings or errors.
    /// </summary>
    public bool HasDynamoWarnings
    {
        get
        {
            if (string.IsNullOrEmpty(ResultJson)) return false;
            try
            {
                using var doc = JsonDocument.Parse(ResultJson);
                if (!doc.RootElement.TryGetProperty("data", out var data)) return false;
                if (data.TryGetProperty("warnings", out var w) && w.GetArrayLength() > 0) return true;
                if (data.TryGetProperty("errors", out var e) && e.GetArrayLength() > 0) return true;
                return false;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Human-readable summary of Dynamo warnings/errors.
    /// </summary>
    public string DynamoWarningsSummary
    {
        get
        {
            if (string.IsNullOrEmpty(ResultJson)) return "";
            try
            {
                using var doc = JsonDocument.Parse(ResultJson);
                if (!doc.RootElement.TryGetProperty("data", out var data)) return "";
                var lines = new List<string>();

                if (data.TryGetProperty("errors", out var errors))
                {
                    foreach (var item in errors.EnumerateArray())
                        lines.Add($"\u2717 {item.GetString()}");
                }
                if (data.TryGetProperty("warnings", out var warnings))
                {
                    foreach (var item in warnings.EnumerateArray())
                        lines.Add($"\u26A0 {item.GetString()}");
                }
                return string.Join("\n", lines);
            }
            catch { return ""; }
        }
    }

    /// <summary>
    /// Icon to display based on event type.
    /// </summary>
    public string StatusIcon => EventType switch
    {
        "started" => "\u25B6", // Play symbol
        "completed" => HasDynamoWarnings ? "\u26A0" : "\u2713", // Warning or Check mark
        "failed" => "\u2717", // X mark
        _ => "\u25CF" // Bullet
    };

    /// <summary>
    /// Color for the status indicator.
    /// </summary>
    public string StatusColor => EventType switch
    {
        "started" => "#FFA500", // Orange
        "completed" => HasDynamoWarnings ? "#FF8F00" : "#4CAF50", // Amber or Green
        "failed" => "#F44336", // Red
        _ => "#9E9E9E" // Gray
    };

    /// <summary>
    /// Color for the inline result summary text.
    /// Amber when completed with Dynamo warnings, green otherwise.
    /// </summary>
    public string ResultColor => (EventType == "completed" && HasDynamoWarnings) ? "#FF8F00" : "#4CAF50";

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
