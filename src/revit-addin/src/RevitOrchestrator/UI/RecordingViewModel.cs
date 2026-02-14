using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using RevitOrchestrator.Models;
using RevitOrchestrator.Pipe;
using RevitOrchestrator.Recording;

namespace RevitOrchestrator.UI;

/// <summary>
/// ViewModel for the Recording tab - manages action recording and pattern detection.
/// </summary>
public sealed class RecordingViewModel : INotifyPropertyChanged
{
    private bool _isRecording;
    private string _statusText = "Ready to record";
    private DetectedPattern? _suggestedPattern;
    private bool _initialized;

    // Capture the dispatcher at construction time — Application.Current
    // can be null in Revit dockable panes.
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public RecordingViewModel()
    {
        StartRecordingCommand = new RelayCommand(OnStartRecording, () => !IsRecording);
        StopRecordingCommand = new RelayCommand(OnStopRecording, () => IsRecording);
        ClearRecordingCommand = new RelayCommand(OnClearRecording, () => Actions.Count > 0);
        SaveAsWorkflowCommand = new RelayCommand(OnSaveAsWorkflow, () => SelectedActions.Any());
        SavePatternCommand = new RelayCommand(OnSavePattern, () => SuggestedPattern != null);
        DismissPatternCommand = new RelayCommand(OnDismissPattern, () => SuggestedPattern != null);

        // Update command states when collection changes, and wire/unwire
        // PropertyChanged on individual items so checkbox toggles propagate.
        Actions.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (RecordedAction item in e.NewItems)
                    item.PropertyChanged += OnActionPropertyChanged;
            if (e.OldItems != null)
                foreach (RecordedAction item in e.OldItems)
                    item.PropertyChanged -= OnActionPropertyChanged;

            (ClearRecordingCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SaveAsWorkflowCommand as RelayCommand)?.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(ActionCount));

            // Check for patterns when we have enough actions
            if (Actions.Count >= 4)
            {
                DetectPatterns();
            }
        };
    }

    private void OnActionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecordedAction.IsSelected))
            NotifySelectionChanged();
    }

    /// <summary>
    /// Initialize with the action recorder.
    /// </summary>
    public void Initialize(ActionRecorder recorder)
    {
        if (_initialized) return;
        _initialized = true;

        recorder.ActionRecorded += OnActionRecorded;
        recorder.RecordingStateChanged += OnRecordingStateChanged;
    }

    /// <summary>
    /// All recorded actions.
    /// </summary>
    public ObservableCollection<RecordedAction> Actions { get; } = new();

    /// <summary>
    /// Whether recording is active.
    /// </summary>
    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            _isRecording = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RecordingButtonText));
            OnPropertyChanged(nameof(RecordingIndicatorVisibility));
            (StartRecordingCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StopRecordingCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Text for the recording toggle button.
    /// </summary>
    public string RecordingButtonText => IsRecording ? "Stop Recording" : "Start Recording";

    /// <summary>
    /// Whether to show the recording indicator.
    /// </summary>
    public bool RecordingIndicatorVisibility => IsRecording;

    /// <summary>
    /// Status text shown at the bottom.
    /// </summary>
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Number of recorded actions.
    /// </summary>
    public int ActionCount => Actions.Count;

    /// <summary>
    /// Whether to show the empty state.
    /// </summary>
    public bool ShowEmptyState => Actions.Count == 0;

    /// <summary>
    /// Actions that are selected for workflow creation.
    /// </summary>
    public IEnumerable<RecordedAction> SelectedActions => Actions.Where(a => a.IsSelected);

    /// <summary>
    /// Number of selected actions.
    /// </summary>
    public int SelectedCount => SelectedActions.Count();

    /// <summary>
    /// A detected pattern suggestion.
    /// </summary>
    public DetectedPattern? SuggestedPattern
    {
        get => _suggestedPattern;
        set
        {
            _suggestedPattern = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSuggestedPattern));
            (SavePatternCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DismissPatternCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Whether there's a pattern suggestion to show.
    /// </summary>
    public bool HasSuggestedPattern => SuggestedPattern != null;

    public ICommand StartRecordingCommand { get; }
    public ICommand StopRecordingCommand { get; }
    public ICommand ClearRecordingCommand { get; }
    public ICommand SaveAsWorkflowCommand { get; }
    public ICommand SavePatternCommand { get; }
    public ICommand DismissPatternCommand { get; }

    /// <summary>
    /// Notify that selection changed on an action.
    /// </summary>
    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        (SaveAsWorkflowCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnActionRecorded(RecordedAction action)
    {
        _dispatcher.Invoke(() =>
        {
            try
            {
                Actions.Add(action);
                StatusText = $"Recorded: {action.Description}";
            }
            catch (Exception)
            {
                // Prevent exceptions from crashing Revit
            }
        });
    }

    private void OnRecordingStateChanged(bool isRecording)
    {
        _dispatcher.Invoke(() =>
        {
            try
            {
                IsRecording = isRecording;
                StatusText = isRecording ? "Recording..." : "Recording stopped";
            }
            catch (Exception)
            {
                // Prevent exceptions from crashing Revit
            }
        });
    }

    private void OnStartRecording()
    {
        try
        {
            if (App.Instance is not { } app) return;

            // Must run on the Revit API thread — subscribing to Revit events
            // from a dockable pane button click is outside the API context.
            app.RunOnRevitThread(_ =>
            {
                app.ActionRecorder?.StartRecording();
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to start recording: {ex.Message}";
        }
    }

    private void OnStopRecording()
    {
        try
        {
            if (App.Instance is not { } app) return;

            app.RunOnRevitThread(_ =>
            {
                app.ActionRecorder?.StopRecording();
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to stop recording: {ex.Message}";
        }
    }

    private void OnClearRecording()
    {
        // Unsubscribe from all items before clearing (Reset doesn't provide OldItems)
        foreach (var action in Actions)
            action.PropertyChanged -= OnActionPropertyChanged;
        Actions.Clear();
        App.Instance?.ActionRecorder?.ClearActions();
        SuggestedPattern = null;
        StatusText = "Recording cleared";
    }

    private async void OnSaveAsWorkflow()
    {
        var selectedActions = SelectedActions.OrderBy(a => a.Timestamp).ToList();
        if (!selectedActions.Any())
        {
            StatusText = "No actions selected";
            return;
        }

        var steps = selectedActions.Select(a => new WorkflowStep
        {
            ToolName = a.MappedToolName ?? "revit.unknown",
            Args = a.Parameters
        }).ToList();

        var vm = new WorkflowEditorViewModel();
        vm.WorkflowDescription = $"Recorded workflow with {selectedActions.Count} steps";
        foreach (var step in steps)
        {
            vm.Steps.Add(new WorkflowStepViewModel(step));
        }
        // Generate default name from first tool
        if (steps.Count > 0)
        {
            var safeName = System.Text.RegularExpressions.Regex.Replace(steps[0].ToolName, @"[^\w\.]", "_");
            vm.WorkflowName = $"recorded.{safeName}";
        }

        var def = await WorkflowEditorDialog.ShowWithTestSupportAsync(vm);
        if (def != null)
        {
            await SaveWorkflowToServerAsync(def.Name, def.Description, def.Steps, def);
        }
    }

    private async void OnSavePattern()
    {
        if (SuggestedPattern == null) return;

        var steps = SuggestedPattern.Actions.Select(a => new WorkflowStep
        {
            ToolName = a.MappedToolName ?? "revit.unknown",
            Args = a.Parameters
        }).ToList();

        var vm = new WorkflowEditorViewModel();
        vm.WorkflowDescription = SuggestedPattern.Description;
        foreach (var step in steps)
        {
            vm.Steps.Add(new WorkflowStepViewModel(step));
        }

        var def = await WorkflowEditorDialog.ShowWithTestSupportAsync(vm);
        if (def != null)
        {
            await SaveWorkflowToServerAsync(def.Name, def.Description, def.Steps, def);
        }

        SuggestedPattern = null;
    }

    private void OnDismissPattern()
    {
        SuggestedPattern = null;
    }

    private async System.Threading.Tasks.Task SaveWorkflowToServerAsync(
        string name, string description, List<WorkflowStep> steps,
        Models.WorkflowDefinition? definition = null)
    {
        if (App.Instance?.PipeListener is not { } listener || !listener.IsConnected)
        {
            StatusText = "Not connected to server";
            return;
        }

        StatusText = "Saving workflow...";

        try
        {
            await listener.SaveWorkflowAsync(name, description, steps, definition);
            StatusText = $"Saved workflow: {name}";

            // Clear selection after saving
            foreach (var action in Actions)
            {
                action.IsSelected = false;
            }
            NotifySelectionChanged();
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Detect repeated patterns in the recorded actions.
    /// </summary>
    private void DetectPatterns()
    {
        var actions = Actions.ToList();
        if (actions.Count < 4) return;

        // Look for repeated sequences of 2-4 actions
        for (int windowSize = 2; windowSize <= 4; windowSize++)
        {
            var pattern = FindRepeatedSequence(actions, windowSize, minOccurrences: 2);
            if (pattern != null)
            {
                SuggestedPattern = pattern;
                return;
            }
        }
    }

    private static DetectedPattern? FindRepeatedSequence(
        List<RecordedAction> actions, int windowSize, int minOccurrences)
    {
        var sequences = new Dictionary<string, List<List<RecordedAction>>>();

        for (int i = 0; i <= actions.Count - windowSize; i++)
        {
            var window = actions.Skip(i).Take(windowSize).ToList();
            var signature = string.Join("|", window.Select(a => a.GetSignature()));

            if (!sequences.ContainsKey(signature))
            {
                sequences[signature] = new List<List<RecordedAction>>();
            }
            sequences[signature].Add(window);
        }

        // Find the most common sequence that meets the threshold
        var mostCommon = sequences
            .Where(kvp => kvp.Value.Count >= minOccurrences)
            .OrderByDescending(kvp => kvp.Value.Count)
            .FirstOrDefault();

        if (mostCommon.Key == null) return null;

        var exampleSequence = mostCommon.Value.First();
        var description = string.Join(" → ", exampleSequence.Select(a =>
            !string.IsNullOrEmpty(a.Category) ? a.Category : a.Description));

        return new DetectedPattern
        {
            Signature = mostCommon.Key,
            Occurrences = mostCommon.Value.Count,
            Description = description,
            Actions = exampleSequence,
        };
    }

    /// <summary>
    /// Handle a workflow suggestion received from the Python server.
    /// </summary>
    public void HandleServerSuggestion(JsonElement payload)
    {
        _dispatcher.Invoke(() =>
        {
            try
            {
                var sequence = new List<string>();
                if (payload.TryGetProperty("sequence", out var seqEl))
                {
                    foreach (var item in seqEl.EnumerateArray())
                    {
                        sequence.Add(item.GetString() ?? "");
                    }
                }

                var description = payload.TryGetProperty("description", out var descEl)
                    ? descEl.GetString() ?? ""
                    : string.Join(" → ", sequence);

                var occurrences = payload.TryGetProperty("occurrences", out var occEl)
                    ? occEl.GetInt32()
                    : 0;

                SuggestedPattern = new DetectedPattern
                {
                    Signature = string.Join("|", sequence),
                    Occurrences = occurrences,
                    Description = $"[Server] {description}",
                    Actions = new List<RecordedAction>(),
                };

                StatusText = "Server detected a workflow pattern";
            }
            catch (Exception ex)
            {
                StatusText = $"Error parsing suggestion: {ex.Message}";
            }
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Represents a detected pattern of repeated actions.
/// </summary>
public sealed class DetectedPattern
{
    public string Signature { get; set; } = string.Empty;
    public int Occurrences { get; set; }
    public string Description { get; set; } = string.Empty;
    public List<RecordedAction> Actions { get; set; } = new();
}
