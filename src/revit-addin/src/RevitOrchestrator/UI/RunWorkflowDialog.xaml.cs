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

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not RunParameterViewModel param)
            return;

        var dlg = new Microsoft.Win32.OpenFileDialog();

        // Set filter based on parameter key
        var lower = param.Key.ToLowerInvariant();
        if (lower.Contains("graph"))
        {
            dlg.Filter = "Dynamo Graphs (*.dyn)|*.dyn|All Files (*.*)|*.*";
            dlg.DefaultExt = ".dyn";
        }
        else if (lower.Contains("script") || lower.Contains("python"))
        {
            dlg.Filter = "Python Scripts (*.py)|*.py|All Files (*.*)|*.*";
            dlg.DefaultExt = ".py";
        }
        else
        {
            dlg.Filter = "All Files (*.*)|*.*";
        }

        // Start in current path's directory if valid
        var current = param.CurrentValueText?.Trim() ?? "";
        if (!string.IsNullOrEmpty(current))
        {
            var dir = System.IO.Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }

        if (dlg.ShowDialog(this) == true)
        {
            param.CurrentValueText = dlg.FileName;
        }
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

            // Trigger initial path validation for pre-filled file paths
            if (param.IsFilePath && !string.IsNullOrEmpty(param.CurrentValueText))
                param.ValidatePath();
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
        if (lower.Contains("path") || lower.Contains("file") || lower.Contains("script"))
            return "file_path";
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
    private string _pathValidationText = "";
    private bool _pathExists;
    private System.Threading.CancellationTokenSource? _pathValidationCts;

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

    /// <summary>True when a plain text box should be shown (no dropdown, not bool, not file).</summary>
    public bool IsTextInput => !HasOptions && !IsBoolType && !IsFilePath;

    /// <summary>True when this parameter should show file path UI (textbox + browse + validation).</summary>
    public bool IsFilePath => UiHint == "file_path";

    /// <summary>Path validation result text.</summary>
    public string PathValidationText
    {
        get => _pathValidationText;
        private set { _pathValidationText = value; OnPropertyChanged(); }
    }

    /// <summary>Whether the current path exists and is valid.</summary>
    public bool PathExists
    {
        get => _pathExists;
        private set { _pathExists = value; OnPropertyChanged(); OnPropertyChanged(nameof(PathIndicator)); OnPropertyChanged(nameof(PathIndicatorColor)); }
    }

    /// <summary>Icon for path validation status.</summary>
    public string PathIndicator => string.IsNullOrWhiteSpace(CurrentValueText) ? "" : PathExists ? "\u2713" : "\u2717";

    /// <summary>Color for the path validation indicator.</summary>
    public string PathIndicatorColor => PathExists ? "#4CAF50" : "#F44336";

    public string CurrentValueText
    {
        get => _currentValueText;
        set
        {
            _currentValueText = value;
            OnPropertyChanged();
            if (IsFilePath) ValidatePathDebounced();
        }
    }

    public bool BoolValue
    {
        get => _boolValue;
        set { _boolValue = value; OnPropertyChanged(); CurrentValueText = value.ToString().ToLower(); }
    }

    /// <summary>
    /// Validate the file path with a short debounce to avoid excessive I/O during typing.
    /// </summary>
    private async void ValidatePathDebounced()
    {
        _pathValidationCts?.Cancel();
        _pathValidationCts = new System.Threading.CancellationTokenSource();
        var token = _pathValidationCts.Token;

        try
        {
            await System.Threading.Tasks.Task.Delay(350, token);
            if (token.IsCancellationRequested) return;
            ValidatePath();
        }
        catch (System.Threading.Tasks.TaskCanceledException) { }
    }

    /// <summary>
    /// Synchronously validate the current path value.
    /// </summary>
    public void ValidatePath()
    {
        var path = CurrentValueText?.Trim() ?? "";
        if (string.IsNullOrEmpty(path))
        {
            PathExists = false;
            PathValidationText = "";
            return;
        }

        // Normalize path
        try { path = System.IO.Path.GetFullPath(path); } catch { }

        // Extension check for Dynamo graphs
        var ext = System.IO.Path.GetExtension(path);
        if (Key.ToLowerInvariant().Contains("graph") && !string.IsNullOrEmpty(ext)
            && !ext.Equals(".dyn", StringComparison.OrdinalIgnoreCase))
        {
            PathExists = false;
            PathValidationText = $"Expected .dyn file, got {ext}";
            return;
        }

        // Existence check
        if (System.IO.File.Exists(path))
        {
            PathExists = true;
            PathValidationText = path;
            return;
        }

        // Check if directory exists (path might be wrong file name)
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
        {
            PathExists = false;
            PathValidationText = "File not found (directory exists)";
        }
        else
        {
            PathExists = false;
            PathValidationText = "Path not found";
        }
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
