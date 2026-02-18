using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using RevitOrchestrator.Models;
using RevitOrchestrator.Pipe;

namespace RevitOrchestrator.UI;

/// <summary>
/// MVVM ViewModel for the History tab.
/// </summary>
public sealed class HistoryViewModel : INotifyPropertyChanged
{
    private ExecutionEvent? _currentExecution;
    private bool _isExecuting;
    private string _statusText = string.Empty;

    // Capture the dispatcher at construction time — Application.Current
    // can be null in Revit dockable panes.
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public HistoryViewModel()
    {
        ClearHistoryCommand = new RelayCommand(OnClearHistory, () => Events.Count > 0);
        SaveAsWorkflowCommand = new RelayCommand(OnSaveAsWorkflow, () => SelectedEvents.Any());
        ToggleExpandCommand = new RelayCommand<ExecutionEvent>(OnToggleExpand);
        CopyErrorCommand = new RelayCommand<ExecutionEvent>(OnCopyError);
        CopyRunDetailsCommand = new RelayCommand<ExecutionEvent>(OnCopyRunDetails);
        OpenInDynamoCommand = new RelayCommand<ExecutionEvent>(OnOpenInDynamo, CanOpenInDynamo);

        // Subscribe to pipe listener events
        if (App.Instance?.PipeListener is { } listener)
        {
            listener.OnExecutionEvent += OnExecutionEvent;
            listener.OnWorkflowSaveResponse += OnWorkflowSaveResponse;
        }

        // Update command states when collection changes, and wire/unwire
        // PropertyChanged on individual items so checkbox toggles propagate.
        Events.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (ExecutionEvent item in e.NewItems)
                    item.PropertyChanged += OnEventPropertyChanged;
            if (e.OldItems != null)
                foreach (ExecutionEvent item in e.OldItems)
                    item.PropertyChanged -= OnEventPropertyChanged;

            (ClearHistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SaveAsWorkflowCommand as RelayCommand)?.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(ShowEmptyState));
        };
    }

    private void OnEventPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExecutionEvent.IsSelected))
            NotifySelectionChanged();
    }

    /// <summary>
    /// All execution events in history.
    /// </summary>
    public ObservableCollection<ExecutionEvent> Events { get; } = new();

    /// <summary>
    /// Currently executing event (for live indicator).
    /// </summary>
    public ExecutionEvent? CurrentExecution
    {
        get => _currentExecution;
        set
        {
            _currentExecution = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsExecuting));
        }
    }

    /// <summary>
    /// Whether a tool is currently executing.
    /// </summary>
    public bool IsExecuting
    {
        get => _isExecuting;
        set { _isExecuting = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Status text displayed in the action bar.
    /// </summary>
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Events that are selected for workflow saving.
    /// </summary>
    public IEnumerable<ExecutionEvent> SelectedEvents => Events.Where(e => e.IsSelected && e.EventType == "completed");

    /// <summary>
    /// Number of selected events.
    /// </summary>
    public int SelectedCount => SelectedEvents.Count();

    /// <summary>
    /// Whether to show the empty state message.
    /// </summary>
    public bool ShowEmptyState => Events.Count == 0;

    public ICommand ClearHistoryCommand { get; }
    public ICommand SaveAsWorkflowCommand { get; }
    public ICommand ToggleExpandCommand { get; }
    public ICommand CopyErrorCommand { get; }
    public ICommand CopyRunDetailsCommand { get; }
    public ICommand OpenInDynamoCommand { get; }

    /// <summary>
    /// Handle an execution event from the pipe.
    /// </summary>
    public void HandleExecutionEvent(JsonElement payload)
    {
        _dispatcher.Invoke(() =>
        {
            var eventId = payload.TryGetProperty("event_id", out var eid) ? eid.GetString() ?? "" : "";
            var eventType = payload.TryGetProperty("event_type", out var et) ? et.GetString() ?? "" : "";
            var toolName = payload.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "" : "";
            var correlationId = payload.TryGetProperty("correlation_id", out var cid) ? cid.GetString() ?? "" : "";
            var sequence = payload.TryGetProperty("sequence", out var seq) ? seq.GetInt32() : 0;
            var durationMs = payload.TryGetProperty("duration_ms", out var dur) ? dur.GetInt32() : 0;
            var errorMessage = payload.TryGetProperty("error", out var err) ? err.GetString() : null;
            var origin = payload.TryGetProperty("origin", out var o) ? o.GetString() ?? "chat" : "chat";

            // Get args as JSON string
            var argsJson = "";
            if (payload.TryGetProperty("args", out var args))
            {
                argsJson = args.GetRawText();
            }

            // Get result as JSON string
            string? resultJson = null;
            if (payload.TryGetProperty("result", out var result) && result.ValueKind != JsonValueKind.Null)
            {
                resultJson = result.GetRawText();
            }

            if (eventType == "started")
            {
                // Add new event
                var evt = new ExecutionEvent
                {
                    EventId = eventId,
                    EventType = eventType,
                    ToolName = toolName,
                    ArgsJson = argsJson,
                    CorrelationId = correlationId,
                    Sequence = sequence,
                    Timestamp = DateTime.Now,
                    Origin = origin,
                };
                Events.Add(evt);

                // Update live indicator
                CurrentExecution = evt;
                IsExecuting = true;
                StatusText = $"Executing {toolName}...";
            }
            else
            {
                // Find existing event and update it
                var existing = Events.FirstOrDefault(e => e.EventId == eventId);
                if (existing != null)
                {
                    existing.EventType = eventType;
                    existing.DurationMs = durationMs;
                    existing.ResultJson = resultJson;
                    existing.ErrorMessage = errorMessage;
                }
                else
                {
                    // Event not found, add as new
                    Events.Add(new ExecutionEvent
                    {
                        EventId = eventId,
                        EventType = eventType,
                        ToolName = toolName,
                        ArgsJson = argsJson,
                        CorrelationId = correlationId,
                        Sequence = sequence,
                        Timestamp = DateTime.Now,
                        DurationMs = durationMs,
                        ResultJson = resultJson,
                        ErrorMessage = errorMessage,
                        Origin = origin,
                    });
                }

                // Clear live indicator
                if (CurrentExecution?.EventId == eventId)
                {
                    CurrentExecution = null;
                    IsExecuting = false;
                }

                StatusText = eventType == "completed"
                    ? $"{toolName} completed in {durationMs}ms"
                    : $"{toolName} failed: {errorMessage}";
            }

            (SaveAsWorkflowCommand as RelayCommand)?.RaiseCanExecuteChanged();
        });
    }

    /// <summary>
    /// Called when selection changes on an event.
    /// </summary>
    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        (SaveAsWorkflowCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnClearHistory()
    {
        // Unsubscribe from all items before clearing (Reset doesn't provide OldItems)
        foreach (var evt in Events)
            evt.PropertyChanged -= OnEventPropertyChanged;
        Events.Clear();
        CurrentExecution = null;
        IsExecuting = false;
        StatusText = "History cleared";
    }

    private void OnToggleExpand(ExecutionEvent? evt)
    {
        if (evt is null) return;
        evt.IsExpanded = !evt.IsExpanded;
    }

    private void OnCopyError(ExecutionEvent? evt)
    {
        if (evt is null || string.IsNullOrEmpty(evt.ErrorMessage)) return;
        try
        {
            System.Windows.Clipboard.SetText(evt.ErrorMessage);
            StatusText = "Error copied to clipboard";
        }
        catch { StatusText = "Failed to copy to clipboard"; }
    }

    private void OnCopyRunDetails(ExecutionEvent? evt)
    {
        if (evt is null) return;
        try
        {
            System.Windows.Clipboard.SetText(evt.RunDetailsText);
            StatusText = "Run details copied to clipboard";
        }
        catch { StatusText = "Failed to copy to clipboard"; }
    }

    private bool CanOpenInDynamo(ExecutionEvent? evt)
        => evt?.IsDynamoTool == true && !string.IsNullOrEmpty(evt.GraphPath);

    private void OnOpenInDynamo(ExecutionEvent? evt)
    {
        if (evt == null) return;
        var graphPath = evt.GraphPath;
        if (string.IsNullOrEmpty(graphPath)) return;

        StatusText = $"Opening in Dynamo: {System.IO.Path.GetFileName(graphPath)}...";

        App.Instance?.RunOnRevitThread(uiApp =>
        {
            try
            {
                Commands.RunDynamoGraphCommand.OpenInDynamoUI(graphPath, uiApp);
                _dispatcher.Invoke(() => StatusText = $"Opened in Dynamo: {System.IO.Path.GetFileName(graphPath)}");
            }
            catch (Exception ex)
            {
                var msg = ex is System.Reflection.TargetInvocationException tie
                    ? (tie.InnerException?.Message ?? ex.Message) : ex.Message;
                _dispatcher.Invoke(() => StatusText = $"Failed to open in Dynamo: {msg}");
            }
        });
    }

    private async void OnSaveAsWorkflow()
    {
        var selectedEvents = SelectedEvents.OrderBy(e => e.Sequence).ToList();
        if (!selectedEvents.Any())
        {
            StatusText = "No completed events selected";
            return;
        }

        // Build workflow steps from selected events
        var steps = selectedEvents.Select(e =>
        {
            var args = new Dictionary<string, object?>();
            if (!string.IsNullOrEmpty(e.ArgsJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(e.ArgsJson);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        args[prop.Name] = GetJsonValue(prop.Value);
                    }
                }
                catch { }
            }

            return new WorkflowStep
            {
                ToolName = e.ToolName,
                Args = args
            };
        }).ToList();

        var vm = new WorkflowEditorViewModel();
        foreach (var step in steps)
        {
            vm.Steps.Add(new WorkflowStepViewModel(step));
        }
        // Generate default name from first tool
        if (steps.Count > 0)
        {
            var safeName = System.Text.RegularExpressions.Regex.Replace(steps[0].ToolName, @"[^\w\.]", "_");
            vm.WorkflowName = $"captured.{safeName}";
        }

        var def = await WorkflowEditorDialog.ShowWithTestSupportAsync(vm);
        if (def != null)
        {
            await SaveWorkflowToServerAsync(def);
        }
    }

    private async Task SaveWorkflowToServerAsync(WorkflowDefinition definition)
    {
        if (App.Instance?.PipeListener is not { } listener || !listener.IsConnected)
        {
            StatusText = "Not connected to server";
            return;
        }

        StatusText = "Saving workflow...";

        try
        {
            await listener.SaveWorkflowAsync(
                definition.Name, definition.Description, definition.Steps, definition);
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    private static object? GetJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(GetJsonValue).ToList(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => GetJsonValue(p.Value)),
            _ => element.GetRawText()
        };
    }

    private void OnExecutionEvent(JsonElement payload)
    {
        HandleExecutionEvent(payload);
    }

    private void OnWorkflowSaveResponse(JsonElement payload)
    {
        _dispatcher.Invoke(() =>
        {
            var success = payload.TryGetProperty("success", out var s) && s.GetBoolean();
            if (success)
            {
                var workflowName = payload.TryGetProperty("workflow_name", out var wn) ? wn.GetString() ?? "" : "";
                StatusText = $"Saved workflow: {workflowName}";

                // Clear selection
                foreach (var evt in Events)
                {
                    evt.IsSelected = false;
                }
                NotifySelectionChanged();
            }
            else
            {
                var error = payload.TryGetProperty("error", out var e) ? e.GetString() ?? "Unknown error" : "Unknown error";
                StatusText = $"Failed to save: {error}";
            }
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
