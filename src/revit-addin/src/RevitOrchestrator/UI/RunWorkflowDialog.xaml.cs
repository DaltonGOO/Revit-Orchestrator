using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using RevitOrchestrator.Models;
using RevitOrchestrator.Pipe;

namespace RevitOrchestrator.UI;

/// <summary>
/// Dialog for running a parameterized workflow with user-supplied input values.
/// </summary>
public partial class RunWorkflowDialog : Window
{
    private readonly RunWorkflowViewModel _viewModel;

    public RunWorkflowDialog(RunWorkflowViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        InitializeComponent();
        SetOwnerToRevitWindow();
    }

    /// <summary>
    /// The input arguments the user wants to run with, or null if cancelled.
    /// </summary>
    public Dictionary<string, object?>? ResultArgs { get; private set; }

    private void SetOwnerToRevitWindow()
    {
        try
        {
            var revitHandle = Process.GetCurrentProcess().MainWindowHandle;
            if (revitHandle != IntPtr.Zero)
            {
                var helper = new WindowInteropHelper(this);
                helper.Owner = revitHandle;
            }
        }
        catch { }
    }

    private void RunButton_Click(object sender, RoutedEventArgs e)
    {
        ResultArgs = _viewModel.CollectInputArgs();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

/// <summary>
/// ViewModel for the RunWorkflowDialog.
/// </summary>
public sealed class RunWorkflowViewModel : INotifyPropertyChanged
{
    private string _statusText = string.Empty;
    private bool _isHeadlessMode = true;
    private bool _isInteractiveMode;

    public string WorkflowName { get; set; } = "";
    public string WorkflowDescription { get; set; } = "";
    public ObservableCollection<RunParameterViewModel> Parameters { get; } = new();
    public bool CanRun => true;

    // Execution mode support
    public List<string> ExecutionModes { get; set; } = new() { "headless" };
    public string? InteractiveHint { get; set; }
    public bool ShowModePicker => ExecutionModes.Count > 1;

    public bool IsHeadlessMode
    {
        get => _isHeadlessMode;
        set { _isHeadlessMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsInteractiveMode)); }
    }

    public bool IsInteractiveMode
    {
        get => _isInteractiveMode;
        set { _isInteractiveMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsHeadlessMode)); }
    }

    public string SelectedMode => IsInteractiveMode ? "interactive" : "headless";

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Build from a workflow definition's promoted parameters.
    /// </summary>
    public static RunWorkflowViewModel FromDefinition(
        string workflowName,
        string description,
        Dictionary<string, PromotedParameter> parameters,
        List<string>? executionModes = null,
        string? interactiveHint = null)
    {
        var vm = new RunWorkflowViewModel
        {
            WorkflowName = workflowName,
            WorkflowDescription = description,
        };

        if (executionModes is { Count: > 0 })
            vm.ExecutionModes = executionModes;

        vm.InteractiveHint = interactiveHint;

        foreach (var kvp in parameters)
        {
            var param = new RunParameterViewModel
            {
                Key = kvp.Key,
                DisplayName = Humanize(kvp.Key),
                Description = kvp.Value.Description,
                Type = kvp.Value.Type,
                DefaultValue = kvp.Value.DefaultValue,
                CurrentValueText = kvp.Value.DefaultValue?.ToString() ?? "",
                UiHint = DetermineUiHint(kvp.Key),
            };
            vm.Parameters.Add(param);
        }

        return vm;
    }

    /// <summary>
    /// Collect the current parameter values into a dispatch-ready dictionary.
    /// </summary>
    public Dictionary<string, object?> CollectInputArgs()
    {
        var args = new Dictionary<string, object?>();
        foreach (var p in Parameters)
        {
            args[p.Key] = p.GetTypedValue();
        }
        return args;
    }

    private static string Humanize(string key)
    {
        return string.Join(" ", key.Split('_'))
            .Replace("  ", " ")
            .Trim();
    }

    private static string? DetermineUiHint(string key)
    {
        var lower = key.ToLowerInvariant();
        if (lower == "level_name") return "level_dropdown";
        if (lower is "type_name" or "wall_type") return "type_dropdown";
        return null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// ViewModel for a single run-time parameter input.
/// </summary>
public sealed class RunParameterViewModel : INotifyPropertyChanged
{
    private string _currentValueText = "";
    private bool _boolValue;

    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "string";
    public object? DefaultValue { get; set; }
    public string? UiHint { get; set; }
    public ObservableCollection<string> Options { get; } = new();

    public string TypeHint => $"({Type})";

    /// <summary>True when there are dropdown options to show.</summary>
    public bool HasOptions => Options.Count > 0 && !IsBoolType;

    /// <summary>True for boolean parameters.</summary>
    public bool IsBoolType => Type == "boolean";

    /// <summary>True when a plain text box should be shown (no dropdown, not bool).</summary>
    public bool IsTextInput => !HasOptions && !IsBoolType;

    public string CurrentValueText
    {
        get => _currentValueText;
        set { _currentValueText = value; OnPropertyChanged(); }
    }

    public bool BoolValue
    {
        get => _boolValue;
        set { _boolValue = value; OnPropertyChanged(); CurrentValueText = value.ToString().ToLower(); }
    }

    /// <summary>
    /// Get the typed value for dispatch.
    /// </summary>
    public object? GetTypedValue()
    {
        if (IsBoolType) return BoolValue;

        var text = CurrentValueText?.Trim() ?? "";
        if (string.IsNullOrEmpty(text)) return DefaultValue;

        return Type switch
        {
            "integer" => long.TryParse(text, out var l) ? l : (object)text,
            "number" => double.TryParse(text, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : (object)text,
            "array" or "object" => TryParseJson(text),
            _ => text,
        };
    }

    private static object? TryParseJson(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<object>(text);
        }
        catch
        {
            return text;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
