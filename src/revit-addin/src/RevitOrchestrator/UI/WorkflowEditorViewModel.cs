using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using RevitOrchestrator.Models;
using RevitOrchestrator.Pipe;

namespace RevitOrchestrator.UI;

/// <summary>
/// ViewModel for the WorkflowEditorDialog.
/// </summary>
public sealed class WorkflowEditorViewModel : INotifyPropertyChanged
{
    private WorkflowStepViewModel? _selectedStep;
    private string _workflowName = string.Empty;
    private string _workflowDescription = string.Empty;
    private string _version = "1.0.0";
    private string _author = Environment.UserName;
    private string _tagsText = string.Empty;
    private string _permissionMode = "write";
    private bool _approvalRequired;
    private bool _requestLlmReview;
    private string? _llmReviewSummary;
    private string _statusText = string.Empty;

    // Test state
    private bool _isTesting;
    private bool? _testPassed;
    private string? _testSummary;
    private int _testStepsCompleted;
    private int _testStepsTotal;
    private int _testDurationMs;

    // Side effect flags
    private bool _sideEffect_Creates;
    private bool _sideEffect_Modifies;
    private bool _sideEffect_Deletes;
    private bool _sideEffect_Parameters;
    private bool _sideEffect_View;
    private bool _sideEffect_FileIo;

    // Derived contract fields
    private string _derivedSideEffectsText = "";
    private string _derivedPermissionMode = "";
    private string _derivedPreconditionsText = "";
    private bool _hasDerivedContract;

    public WorkflowEditorViewModel()
    {
        AddStepCommand = new RelayCommand(OnAddStep);
        RemoveStepCommand = new RelayCommand(OnRemoveStep, () => SelectedStep != null);
        MoveUpCommand = new RelayCommand(OnMoveUp, () => SelectedStep != null && Steps.IndexOf(SelectedStep) > 0);
        MoveDownCommand = new RelayCommand(OnMoveDown, () => SelectedStep != null && Steps.IndexOf(SelectedStep) < Steps.Count - 1);
        TestCommand = new RelayCommand(() => { }, () => CanTest);  // Actual execution is handled in the dialog code-behind

        Steps.CollectionChanged += (_, _) =>
        {
            UpdateStepIndices();
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(CanTest));
            (RemoveStepCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (MoveUpCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (MoveDownCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (TestCommand as RelayCommand)?.RaiseCanExecuteChanged();
            RefreshPromotedParameters();
        };
    }

    /// <summary>
    /// Create a ViewModel from an existing workflow definition (for editing).
    /// </summary>
    public WorkflowEditorViewModel(WorkflowDefinition definition) : this()
    {
        WorkflowName = definition.Name;
        WorkflowDescription = definition.Description;
        Version = definition.Version;
        Author = definition.Author ?? Environment.UserName;
        TagsText = string.Join(", ", definition.Tags);
        PermissionMode = definition.PermissionMode;
        ApprovalRequired = definition.ApprovalRequired;

        foreach (var sideEffect in definition.SideEffects)
        {
            switch (sideEffect)
            {
                case "creates_elements": SideEffect_Creates = true; break;
                case "modifies_elements": SideEffect_Modifies = true; break;
                case "deletes_elements": SideEffect_Deletes = true; break;
                case "modifies_parameters": SideEffect_Parameters = true; break;
                case "changes_view": SideEffect_View = true; break;
                case "file_io": SideEffect_FileIo = true; break;
            }
        }

        // Collect promoted parameter keys from the definition
        var promotedKeys = new HashSet<string>(definition.Parameters.Keys, StringComparer.Ordinal);

        foreach (var step in definition.Steps)
        {
            var stepVm = new WorkflowStepViewModel(step, promotedKeys);
            stepVm.OnPromotionChanged = RefreshPromotedParameters;
            Steps.Add(stepVm);
        }

        if (definition.WasLlmReviewed)
        {
            _llmReviewSummary = definition.LlmReviewSummary;
            foreach (var flag in definition.LlmReviewFlags)
            {
                LlmReviewFlags.Add(flag);
            }
        }

        // Populate derived contract fields
        if (definition.DerivedSideEffects.Count > 0 || definition.DerivedPermissionMode != null)
        {
            HasDerivedContract = true;
            DerivedSideEffectsText = string.Join(", ", definition.DerivedSideEffects);
            DerivedPermissionMode = definition.DerivedPermissionMode ?? "read";
            DerivedPreconditionsText = definition.DerivedPreconditions.Count > 0
                ? string.Join(", ", definition.DerivedPreconditions) : "";
        }

        RefreshPromotedParameters();
    }

    public ObservableCollection<WorkflowStepViewModel> Steps { get; } = new();
    public ObservableCollection<string> LlmReviewFlags { get; } = new();

    /// <summary>
    /// Aggregated summary of all promoted parameters across all steps.
    /// </summary>
    public ObservableCollection<PromotedParameterSummary> PromotedParameters { get; } = new();

    public bool HasPromotedParameters => PromotedParameters.Count > 0;

    public WorkflowStepViewModel? SelectedStep
    {
        get => _selectedStep;
        set
        {
            _selectedStep = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedStep));
            (RemoveStepCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (MoveUpCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (MoveDownCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelectedStep => SelectedStep != null;

    public string WorkflowName
    {
        get => _workflowName;
        set { _workflowName = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSave)); }
    }

    public string WorkflowDescription
    {
        get => _workflowDescription;
        set { _workflowDescription = value; OnPropertyChanged(); }
    }

    public string Version
    {
        get => _version;
        set { _version = value; OnPropertyChanged(); }
    }

    public string Author
    {
        get => _author;
        set { _author = value; OnPropertyChanged(); }
    }

    public string TagsText
    {
        get => _tagsText;
        set { _tagsText = value; OnPropertyChanged(); }
    }

    public string PermissionMode
    {
        get => _permissionMode;
        set { _permissionMode = value; OnPropertyChanged(); }
    }

    public bool ApprovalRequired
    {
        get => _approvalRequired;
        set { _approvalRequired = value; OnPropertyChanged(); }
    }

    public bool RequestLlmReview
    {
        get => _requestLlmReview;
        set { _requestLlmReview = value; OnPropertyChanged(); }
    }

    public string? LlmReviewSummary
    {
        get => _llmReviewSummary;
        set { _llmReviewSummary = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasReviewResults)); }
    }

    public bool HasReviewResults => !string.IsNullOrEmpty(LlmReviewSummary);

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public bool CanSave => !string.IsNullOrWhiteSpace(WorkflowName) && Steps.Count > 0;

    // Side effect properties
    public bool SideEffect_Creates { get => _sideEffect_Creates; set { _sideEffect_Creates = value; OnPropertyChanged(); } }
    public bool SideEffect_Modifies { get => _sideEffect_Modifies; set { _sideEffect_Modifies = value; OnPropertyChanged(); } }
    public bool SideEffect_Deletes { get => _sideEffect_Deletes; set { _sideEffect_Deletes = value; OnPropertyChanged(); } }
    public bool SideEffect_Parameters { get => _sideEffect_Parameters; set { _sideEffect_Parameters = value; OnPropertyChanged(); } }
    public bool SideEffect_View { get => _sideEffect_View; set { _sideEffect_View = value; OnPropertyChanged(); } }
    public bool SideEffect_FileIo { get => _sideEffect_FileIo; set { _sideEffect_FileIo = value; OnPropertyChanged(); } }

    // Derived contract properties (read-only in UI)
    public string DerivedSideEffectsText { get => _derivedSideEffectsText; set { _derivedSideEffectsText = value; OnPropertyChanged(); } }
    public string DerivedPermissionMode { get => _derivedPermissionMode; set { _derivedPermissionMode = value; OnPropertyChanged(); } }
    public string DerivedPreconditionsText { get => _derivedPreconditionsText; set { _derivedPreconditionsText = value; OnPropertyChanged(); } }
    public bool HasDerivedContract { get => _hasDerivedContract; set { _hasDerivedContract = value; OnPropertyChanged(); } }

    public ICommand AddStepCommand { get; }
    public ICommand RemoveStepCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand TestCommand { get; private set; } = null!;

    // Test properties
    public bool IsTesting
    {
        get => _isTesting;
        set { _isTesting = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanTest)); }
    }

    public bool CanTest => !IsTesting && Steps.Count > 0;

    public bool? TestPassed
    {
        get => _testPassed;
        set { _testPassed = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasTestResults)); OnPropertyChanged(nameof(TestStatusText)); }
    }

    public bool HasTestResults => TestPassed.HasValue;

    public string? TestSummary
    {
        get => _testSummary;
        set { _testSummary = value; OnPropertyChanged(); }
    }

    public int TestStepsCompleted
    {
        get => _testStepsCompleted;
        set { _testStepsCompleted = value; OnPropertyChanged(); OnPropertyChanged(nameof(TestStatusText)); }
    }

    public int TestStepsTotal
    {
        get => _testStepsTotal;
        set { _testStepsTotal = value; OnPropertyChanged(); OnPropertyChanged(nameof(TestStatusText)); }
    }

    public int TestDurationMs
    {
        get => _testDurationMs;
        set { _testDurationMs = value; OnPropertyChanged(); OnPropertyChanged(nameof(TestStatusText)); }
    }

    public string TestStatusText
    {
        get
        {
            if (IsTesting) return "Running test...";
            if (!TestPassed.HasValue) return "";
            if (TestPassed.Value)
                return $"PASSED - {TestStepsCompleted}/{TestStepsTotal} steps completed in {TestDurationMs}ms";
            return $"FAILED - {TestStepsCompleted}/{TestStepsTotal} steps completed in {TestDurationMs}ms";
        }
    }

    /// <summary>
    /// Set test results from the server response.
    /// </summary>
    public void SetTestResults(bool success, int stepsCompleted, int stepsTotal, int durationMs, string? error)
    {
        IsTesting = false;
        TestPassed = success;
        TestStepsCompleted = stepsCompleted;
        TestStepsTotal = stepsTotal;
        TestDurationMs = durationMs;
        TestSummary = error;
        StatusText = success
            ? $"Test passed! {stepsCompleted}/{stepsTotal} steps completed in {durationMs}ms"
            : $"Test failed: {error ?? "Unknown error"}";
        (TestCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Set LLM review results from the server response.
    /// </summary>
    public void SetReviewResults(string summary, List<string> flags)
    {
        LlmReviewSummary = summary;
        LlmReviewFlags.Clear();
        foreach (var flag in flags)
        {
            LlmReviewFlags.Add(flag);
        }
    }

    /// <summary>
    /// Rebuild the aggregated PromotedParameters collection from all steps.
    /// Called when steps change or when a step arg's IsPromoted changes.
    /// </summary>
    public void RefreshPromotedParameters()
    {
        PromotedParameters.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in Steps)
        {
            foreach (var arg in step.Args)
            {
                if (arg.IsPromoted && seen.Add(arg.Key))
                {
                    PromotedParameters.Add(new PromotedParameterSummary
                    {
                        Key = arg.Key,
                        InferredType = InferType(arg.ValueJson),
                        DefaultValue = arg.ValueJson,
                    });
                }
            }
        }
        OnPropertyChanged(nameof(HasPromotedParameters));
    }

    /// <summary>
    /// Build the list of WorkflowStep objects from the current ViewModel state.
    /// </summary>
    public List<WorkflowStep> BuildSteps()
    {
        return Steps.Select((s, i) => s.ToWorkflowStep($"step{i + 1}")).ToList();
    }

    /// <summary>
    /// Build a complete WorkflowDefinition from the current ViewModel state.
    /// </summary>
    public WorkflowDefinition BuildDefinition()
    {
        var tags = string.IsNullOrWhiteSpace(TagsText)
            ? new List<string>()
            : TagsText.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

        var sideEffects = new List<string>();
        if (SideEffect_Creates) sideEffects.Add("creates_elements");
        if (SideEffect_Modifies) sideEffects.Add("modifies_elements");
        if (SideEffect_Deletes) sideEffects.Add("deletes_elements");
        if (SideEffect_Parameters) sideEffects.Add("modifies_parameters");
        if (SideEffect_View) sideEffects.Add("changes_view");
        if (SideEffect_FileIo) sideEffects.Add("file_io");

        // Compute promoted parameters from all steps
        var parameters = new Dictionary<string, PromotedParameter>();
        foreach (var step in Steps)
        {
            foreach (var arg in step.Args.Where(a => a.IsPromoted))
            {
                if (!parameters.ContainsKey(arg.Key))
                {
                    parameters[arg.Key] = new PromotedParameter
                    {
                        Type = InferType(arg.ValueJson),
                        Description = $"Workflow parameter: {arg.Key}",
                        DefaultValue = ParseJsonValue(arg.ValueJson),
                    };
                }
            }
        }

        return new WorkflowDefinition
        {
            Name = WorkflowName,
            Description = WorkflowDescription,
            Steps = BuildSteps(),
            Parameters = parameters,
            Author = Author,
            Tags = tags,
            PermissionMode = PermissionMode,
            ApprovalRequired = ApprovalRequired,
            SideEffects = sideEffects,
            Version = Version,
            LlmReviewSummary = LlmReviewSummary,
            LlmReviewFlags = LlmReviewFlags.ToList(),
            WasLlmReviewed = HasReviewResults,
        };
    }

    /// <summary>
    /// Infer a JSON Schema type string from a JSON value string.
    /// </summary>
    internal static string InferType(string valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
            return "string";

        var trimmed = valueJson.Trim();

        if (trimmed.StartsWith('['))
            return "array";
        if (trimmed.StartsWith('{'))
            return "object";
        if (trimmed is "true" or "false")
            return "boolean";
        if (double.TryParse(trimmed, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return trimmed.Contains('.') ? "number" : "integer";
        }
        return "string";
    }

    /// <summary>
    /// Parse a JSON value string into a .NET object for serialization.
    /// </summary>
    internal static object? ParseJsonValue(string valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
            return null;
        try
        {
            return JsonSerializer.Deserialize<object>(valueJson);
        }
        catch
        {
            // Not valid JSON — treat as a raw string
            return valueJson;
        }
    }

    private void OnAddStep()
    {
        var step = new WorkflowStepViewModel
        {
            ToolName = "revit.new_tool",
            OnFailure = "stop",
            OnPromotionChanged = RefreshPromotedParameters,
        };
        Steps.Add(step);
        SelectedStep = step;
    }

    private void OnRemoveStep()
    {
        if (SelectedStep == null) return;
        var index = Steps.IndexOf(SelectedStep);
        Steps.Remove(SelectedStep);
        if (Steps.Count > 0)
        {
            SelectedStep = Steps[Math.Min(index, Steps.Count - 1)];
        }
    }

    private void OnMoveUp()
    {
        if (SelectedStep == null) return;
        var index = Steps.IndexOf(SelectedStep);
        if (index <= 0) return;
        Steps.Move(index, index - 1);
    }

    private void OnMoveDown()
    {
        if (SelectedStep == null) return;
        var index = Steps.IndexOf(SelectedStep);
        if (index >= Steps.Count - 1) return;
        Steps.Move(index, index + 1);
    }

    private void UpdateStepIndices()
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            Steps[i].DisplayIndex = i + 1;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Summary of a promoted parameter shown in the right panel.
/// </summary>
public sealed class PromotedParameterSummary
{
    public string Key { get; set; } = "";
    public string InferredType { get; set; } = "string";
    public string DefaultValue { get; set; } = "";
}

/// <summary>
/// ViewModel for a single argument within a workflow step.
/// Supports promotion to workflow-level input via a checkbox.
/// </summary>
public sealed class StepArgViewModel : INotifyPropertyChanged
{
    private string _key = string.Empty;
    private string _valueJson = string.Empty;
    private bool _isPromoted;

    /// <summary>
    /// Callback invoked when IsPromoted changes, so the parent ViewModel can refresh.
    /// </summary>
    public Action? OnPromotionChanged { get; set; }

    public string Key
    {
        get => _key;
        set { _key = value; OnPropertyChanged(); }
    }

    public string ValueJson
    {
        get => _valueJson;
        set { _valueJson = value; OnPropertyChanged(); }
    }

    public bool IsPromoted
    {
        get => _isPromoted;
        set
        {
            _isPromoted = value;
            OnPropertyChanged();
            OnPromotionChanged?.Invoke();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// ViewModel for a single workflow step, supporting editing in the UI.
/// </summary>
public sealed class WorkflowStepViewModel : INotifyPropertyChanged
{
    private int _displayIndex;
    private string _toolName = string.Empty;
    private string _bindingsJson = "{}";
    private string? _guard;
    private string _onFailure = "stop";
    private int _maxRetries;
    private int? _timeoutMs;
    private bool _isWorkflowTool;
    private string _sourceType = "tool";
    private string _sourceAdapter = "";

    /// <summary>
    /// Callback invoked when a step arg's IsPromoted changes.
    /// Wired by WorkflowEditorViewModel to refresh the promoted summary.
    /// </summary>
    public Action? OnPromotionChanged { get; set; }

    public int DisplayIndex
    {
        get => _displayIndex;
        set { _displayIndex = value; OnPropertyChanged(); }
    }

    public string ToolName
    {
        get => _toolName;
        set { _toolName = value; OnPropertyChanged(); OnPropertyChanged(nameof(BadgeText)); }
    }

    /// <summary>
    /// Per-argument collection replacing the old ArgsJson text box.
    /// </summary>
    public ObservableCollection<StepArgViewModel> Args { get; } = new();

    /// <summary>
    /// Step-to-step bindings (advanced). Does NOT include $input bindings — those are
    /// derived from promoted args automatically.
    /// </summary>
    public string BindingsJson
    {
        get => _bindingsJson;
        set { _bindingsJson = value; OnPropertyChanged(); }
    }

    public string? Guard
    {
        get => _guard;
        set { _guard = value; OnPropertyChanged(); }
    }

    public string OnFailure
    {
        get => _onFailure;
        set { _onFailure = value; OnPropertyChanged(); }
    }

    public int MaxRetries
    {
        get => _maxRetries;
        set { _maxRetries = value; OnPropertyChanged(); OnPropertyChanged(nameof(MaxRetriesText)); }
    }

    public string MaxRetriesText
    {
        get => _maxRetries.ToString();
        set { if (int.TryParse(value, out var v)) MaxRetries = v; }
    }

    public int? TimeoutMs
    {
        get => _timeoutMs;
        set { _timeoutMs = value; OnPropertyChanged(); OnPropertyChanged(nameof(TimeoutMsText)); }
    }

    /// <summary>Whether this step references another workflow tool (for visual indicator).</summary>
    public bool IsWorkflowTool
    {
        get => _isWorkflowTool;
        set { _isWorkflowTool = value; OnPropertyChanged(); }
    }

    public string SourceType
    {
        get => _sourceType;
        set { _sourceType = value; OnPropertyChanged(); OnPropertyChanged(nameof(BadgeText)); }
    }

    public string SourceAdapter
    {
        get => _sourceAdapter;
        set { _sourceAdapter = value; OnPropertyChanged(); OnPropertyChanged(nameof(BadgeText)); }
    }

    /// <summary>
    /// Single badge text: "recorded", "composed", "dynamo", "pyrevit", or "revit".
    /// </summary>
    public string BadgeText
    {
        get
        {
            if (!string.IsNullOrEmpty(SourceAdapter) && SourceAdapter != "workflow")
                return SourceAdapter;
            if (SourceType == "recording" || ToolName.StartsWith("recorded.") || ToolName.StartsWith("captured."))
                return "recorded";
            return "composed";
        }
    }

    /// <summary>Raw snapshot preserved for round-trip.</summary>
    public Dictionary<string, object?>? Snapshot { get; set; }

    public string TimeoutMsText
    {
        get => _timeoutMs?.ToString() ?? "";
        set { TimeoutMs = int.TryParse(value, out var v) ? v : null; }
    }

    public string ArgsSummary
    {
        get
        {
            var count = Args.Count;
            var promoted = Args.Count(a => a.IsPromoted);
            if (count == 0) return "no parameters";
            if (promoted == 0) return $"{count} parameter(s)";
            return $"{count} parameter(s), {promoted} promoted";
        }
    }

    /// <summary>
    /// Create from an existing WorkflowStep, with optional set of promoted keys.
    /// </summary>
    public WorkflowStepViewModel(WorkflowStep step, ISet<string>? promotedKeys = null)
    {
        ToolName = step.ToolName;

        // Build per-arg ViewModels from the step's Args dict
        if (step.Args is { Count: > 0 })
        {
            foreach (var kvp in step.Args)
            {
                var isPromoted = false;

                // Check if this arg has a $input binding (from loaded workflow)
                if (step.Bindings != null && step.Bindings.TryGetValue(kvp.Key, out var binding)
                    && binding.StartsWith("$input."))
                {
                    isPromoted = true;
                }
                // Check if the key was in the workflow's promoted parameters
                else if (promotedKeys != null && promotedKeys.Contains(kvp.Key))
                {
                    isPromoted = true;
                }
                // Auto-promote known candidates for new workflows
                else if (promotedKeys == null && AutoPromotionHelper.ShouldAutoPromote(kvp.Key))
                {
                    isPromoted = true;
                }

                var argVm = new StepArgViewModel
                {
                    Key = kvp.Key,
                    ValueJson = SerializeValue(kvp.Value),
                    IsPromoted = isPromoted,
                    OnPromotionChanged = () =>
                    {
                        OnPropertyChanged(nameof(ArgsSummary));
                        OnPromotionChanged?.Invoke();
                    },
                };
                Args.Add(argVm);
            }
        }

        // Build step-to-step bindings (exclude $input.xxx which are promotion bindings)
        if (step.Bindings is { Count: > 0 })
        {
            var stepBindings = step.Bindings
                .Where(b => !b.Value.StartsWith("$input."))
                .ToDictionary(b => b.Key, b => b.Value);
            BindingsJson = stepBindings.Count > 0
                ? JsonSerializer.Serialize(stepBindings, new JsonSerializerOptions { WriteIndented = false })
                : "{}";
        }
        else
        {
            BindingsJson = "{}";
        }

        Guard = step.Guard;
        OnFailure = step.OnFailure;
        MaxRetries = step.MaxRetries;
        TimeoutMs = step.TimeoutMs;
        SourceType = step.SourceType;
        SourceAdapter = step.SourceAdapter;
        Snapshot = step.Snapshot;
    }

    /// <summary>
    /// Default constructor for creating blank steps.
    /// </summary>
    public WorkflowStepViewModel()
    {
    }

    /// <summary>
    /// Convert back to a WorkflowStep data model.
    /// Promoted args stay in Args (as defaults) and also get $input bindings.
    /// </summary>
    public WorkflowStep ToWorkflowStep(string id)
    {
        var args = new Dictionary<string, object?>();
        var bindings = new Dictionary<string, string>();

        // Populate args from per-arg ViewModels
        foreach (var arg in Args)
        {
            args[arg.Key] = ParseJsonValue(arg.ValueJson);
            if (arg.IsPromoted)
            {
                bindings[arg.Key] = $"$input.{arg.Key}";
            }
        }

        // Merge in any step-to-step bindings from the advanced field
        try
        {
            using var doc = JsonDocument.Parse(BindingsJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // Don't overwrite $input bindings
                if (!bindings.ContainsKey(prop.Name))
                {
                    bindings[prop.Name] = prop.Value.GetString() ?? "";
                }
            }
        }
        catch { }

        return new WorkflowStep
        {
            Id = id,
            ToolName = ToolName,
            Args = args,
            Bindings = bindings.Count > 0 ? bindings : null,
            Guard = string.IsNullOrWhiteSpace(Guard) ? null : Guard,
            OnFailure = OnFailure,
            MaxRetries = MaxRetries,
            TimeoutMs = TimeoutMs,
            Snapshot = Snapshot,
            SourceType = SourceType,
            SourceAdapter = SourceAdapter,
        };
    }

    private static string SerializeValue(object? value)
    {
        if (value is null) return "";
        if (value is string s) return s;
        // Handle JsonElement directly to avoid double-quoting strings
        if (value is JsonElement elem)
        {
            return elem.ValueKind == JsonValueKind.String
                ? elem.GetString() ?? ""
                : elem.GetRawText();
        }
        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch
        {
            return value.ToString() ?? "";
        }
    }

    private static object? ParseJsonValue(string valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
            return null;

        var trimmed = valueJson.Trim();

        // Handle JSON-quoted strings (e.g. "\"Level 1\"")
        if (trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Length >= 2)
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return doc.RootElement.GetString();
            }
            catch { }
        }

        // Try to parse as JSON first (arrays, objects, numbers, booleans)
        if (trimmed.StartsWith('[') || trimmed.StartsWith('{') ||
            trimmed is "true" or "false" or "null" ||
            double.TryParse(trimmed, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return GetJsonValue(doc.RootElement);
            }
            catch { }
        }

        // Plain string value
        return valueJson;
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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
