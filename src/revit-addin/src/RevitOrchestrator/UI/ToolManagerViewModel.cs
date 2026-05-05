using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using RevitOrchestrator.Models;
using RevitOrchestrator.Pipe;

namespace RevitOrchestrator.UI;

/// <summary>
/// MVVM ViewModel for the Tools tab.
/// </summary>
public sealed class ToolManagerViewModel : INotifyPropertyChanged
{
    private static readonly string FavoritesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RevitOrchestrator", "favorites.json");

    private bool _isLoading;
    private ToolInfo? _selectedTool;
    private string _statusText = string.Empty;
    private string _searchText = string.Empty;
    private string _selectedCategory = "All";
    private bool _isComposeMode;
    private int _selectedForComposeCount;
    private static List<ToolInfo>? _cachedTools;
    private readonly HashSet<string> _favorites;

    // Capture the dispatcher at construction time — Application.Current
    // can be null in Revit dockable panes.
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public ToolManagerViewModel()
    {
        _favorites = LoadFavorites();

        RefreshCommand = new RelayCommand(OnRefresh);
        AddToolCommand = new RelayCommand(OnAddTool);
        DeleteToolCommand = new RelayCommand(OnDeleteTool, () => SelectedTool != null);
        EditToolCommand = new RelayCommand(OnEditTool, () => SelectedTool != null);
        RunToolCommand = new RelayCommand(OnRunTool, () => SelectedTool != null);
        RunDynamoDialogCommand = new RelayCommand(OnRunDynamoDialog, () => IsDynamoToolSelected);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        ToggleFavoriteCommand = new RelayCommand<ToolInfo>(OnToggleFavorite);
        ToggleComposeModeCommand = new RelayCommand(OnToggleComposeMode);
        ComposeWorkflowCommand = new RelayCommand(OnComposeWorkflow, () => CanCreateWorkflow);

        ToolsView = CollectionViewSource.GetDefaultView(Tools);
        ToolsView.Filter = FilterTool;
        ToolsView.SortDescriptions.Add(new SortDescription(nameof(ToolInfo.IsFavorite), ListSortDirection.Descending));

        // Subscribe to pipe listener events
        if (App.Instance?.PipeListener is { } listener)
        {
            listener.OnToolListResponse += OnToolListResponse;
            listener.OnToolAddResponse += OnToolAddResponse;
            listener.OnToolDeleteResponse += OnToolDeleteResponse;
            listener.OnWorkflowLoadResponse += OnWorkflowLoadResponse;
            listener.OnToolLoadResponse += OnToolLoadResponse;
            listener.OnToolUpdateResponse += OnToolUpdateResponse;
        }
    }

    public ObservableCollection<ToolInfo> Tools { get; } = new();

    public ICollectionView ToolsView { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (_selectedCategory == value) return;
            _selectedCategory = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAllCategory));
            OnPropertyChanged(nameof(IsAtomicCategory));
            OnPropertyChanged(nameof(IsWorkflowCategory));
            ApplyFilter();
        }
    }

    public bool IsAllCategory
    {
        get => _selectedCategory == "All";
        set { if (value) SelectedCategory = "All"; }
    }

    public bool IsAtomicCategory
    {
        get => _selectedCategory == "Atomic Tools";
        set { if (value) SelectedCategory = "Atomic Tools"; }
    }

    public bool IsWorkflowCategory
    {
        get => _selectedCategory == "Workflows";
        set { if (value) SelectedCategory = "Workflows"; }
    }

    public ToolInfo? SelectedTool
    {
        get => _selectedTool;
        set
        {
            _selectedTool = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDynamoToolSelected));
            (DeleteToolCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (EditToolCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RunToolCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RunDynamoDialogCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>True when the selected tool is a Dynamo tool — drives the Run Graph button visibility.</summary>
    public bool IsDynamoToolSelected =>
        SelectedTool != null
        && string.Equals(SelectedTool.Adapter, "dynamo", StringComparison.OrdinalIgnoreCase);

    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowEmptyState)); }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public bool ShowEmptyState => !IsLoading && Tools.Count == 0 && string.IsNullOrEmpty(SearchText);

    public bool ShowNoResults => !IsLoading && Tools.Count > 0 && !string.IsNullOrEmpty(SearchText) && ToolsView.IsEmpty;

    public bool IsComposeMode
    {
        get => _isComposeMode;
        set
        {
            if (_isComposeMode == value) return;
            _isComposeMode = value;
            OnPropertyChanged();
            if (!value) ClearSelections();
            OnPropertyChanged(nameof(CanCreateWorkflow));
        }
    }

    public int SelectedForComposeCount
    {
        get => _selectedForComposeCount;
        private set
        {
            if (_selectedForComposeCount == value) return;
            _selectedForComposeCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanCreateWorkflow));
            OnPropertyChanged(nameof(ComposeStatusText));
            (ComposeWorkflowCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool CanCreateWorkflow => IsComposeMode && SelectedForComposeCount >= 2;

    public string ComposeStatusText => SelectedForComposeCount == 0
        ? "Select tools to compose into a workflow"
        : $"{SelectedForComposeCount} tool(s) selected";

    public ICommand RefreshCommand { get; }
    public ICommand AddToolCommand { get; }
    public ICommand DeleteToolCommand { get; }
    public ICommand EditToolCommand { get; }
    public ICommand RunToolCommand { get; }
    public ICommand RunDynamoDialogCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand ToggleComposeModeCommand { get; }
    public ICommand ComposeWorkflowCommand { get; }

    private bool FilterTool(object obj)
    {
        if (obj is not ToolInfo tool) return false;

        // Hide non-user tools from the UI
        if (tool.Visibility != "user") return false;

        // Category filter
        var isWorkflow = tool.BadgeText is "recorded" or "composed";
        if (_selectedCategory == "Workflows" && !isWorkflow) return false;
        if (_selectedCategory == "Atomic Tools" && isWorkflow) return false;

        if (string.IsNullOrEmpty(_searchText))
            return true;

        return tool.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
            || tool.Description.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
            || tool.BadgeText.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyFilter()
    {
        ToolsView.Refresh();
        UpdateStatusText();
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowNoResults));
    }

    private void UpdateStatusText()
    {
        if (Tools.Count == 0)
            return;

        var filtered = ToolsView.Cast<object>().Count();

        if (_selectedCategory != "All")
        {
            StatusText = $"{filtered} of {Tools.Count} tool(s) in '{_selectedCategory}'";
        }
        else if (string.IsNullOrEmpty(_searchText))
        {
            StatusText = $"{filtered} tool(s) registered";
        }
        else
        {
            StatusText = $"{filtered} of {Tools.Count} tool(s) matching '{_searchText}'";
        }
    }

    public async void RequestRefresh()
    {
        if (App.Instance?.PipeListener is not { } listener || !listener.IsConnected)
        {
            if (_cachedTools is { Count: > 0 })
            {
                foreach (var old in Tools)
                    old.PropertyChanged -= OnToolIsSelectedChanged;
                Tools.Clear();
                foreach (var tool in _cachedTools)
                {
                    tool.PropertyChanged += OnToolIsSelectedChanged;
                    Tools.Add(tool);
                }
                StatusText = $"{_cachedTools.Count} tool(s) (cached - not connected)";
                ApplyFavorites();
                ApplyFilter();
            }
            else
            {
                StatusText = "Not connected - no cached tools available";
            }
            return;
        }

        IsLoading = true;
        StatusText = string.Empty;
        try
        {
            await listener.RequestToolListAsync();
        }
        catch (Exception ex)
        {
            IsLoading = false;
            StatusText = $"Error: {ex.Message}";
        }
    }

    private void OnRefresh()
    {
        RequestRefresh();
    }

    private async void OnAddTool()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Tool File",
            Filter = "All Supported|*.dyn;*.py;*.cs|Dynamo Graphs (*.dyn)|*.dyn|pyRevit Scripts (*.py)|*.py|C# Commands (*.cs)|*.cs",
            FilterIndex = 1,
        };

        if (dialog.ShowDialog() != true)
            return;

        if (App.Instance?.PipeListener is not { } listener || !listener.IsConnected)
        {
            StatusText = "Not connected to server";
            return;
        }

        IsLoading = true;
        StatusText = "Adding tool...";
        try
        {
            await listener.AddToolAsync(dialog.FileName);
        }
        catch (Exception ex)
        {
            IsLoading = false;
            StatusText = $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Open the Dynamo Run Graph dialog for the selected dynamo-adapter tool.
    ///
    /// If the tool's <c>graph_path</c> parameter declares a JSON Schema
    /// <c>default</c> (wrapper tools like <c>dynamo.update_parameter_value</c>
    /// do), the dialog opens with that path pre-filled and auto-discovers the
    /// graph's inputs — the user just fills them in and clicks Run. The
    /// generic <c>dynamo.run_graph</c> tool has no default, so the dialog
    /// opens empty and the user picks a .dyn manually.
    /// </summary>
    private void OnRunDynamoDialog()
    {
        if (!IsDynamoToolSelected) return;

        string? graphPath = null;
        if (SelectedTool?.ParameterSchema.TryGetValue("graph_path", out var pp) == true
            && pp.DefaultValue is string dv && !string.IsNullOrWhiteSpace(dv))
        {
            graphPath = dv;
        }

        try
        {
            var dialog = new RunDynamoGraphDialog(graphPath: graphPath, suggestedInputs: null);
            dialog.Topmost = true;
            dialog.ShowDialog();
            // The dialog itself dispatches the run; nothing more to do here.
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open Run Graph dialog: {ex.Message}";
        }
    }

    private async void OnRunTool()
    {
        if (SelectedTool is null)
            return;

        if (App.Instance?.PipeListener is not { } listener || !listener.IsConnected)
        {
            StatusText = "Not connected to server";
            return;
        }

        var toolName = SelectedTool.Name;
        var parameterSchema = SelectedTool.ParameterSchema;
        var executionModes = SelectedTool.ExecutionModes;

        // If no parameters and only one execution mode, execute directly without a dialog
        if (parameterSchema.Count == 0 && executionModes.Count <= 1)
        {
            StatusText = $"Running {toolName}...";
            var mode = executionModes.Count > 0 ? executionModes[0] : "headless";
            await ExecuteToolRunAsync(listener, toolName, new Dictionary<string, object?>(), mode);
            return;
        }

        // Show parameter input dialog
        var vm = RunWorkflowViewModel.FromDefinition(
            toolName, SelectedTool.Description, parameterSchema,
            executionModes, SelectedTool.InteractiveHint);

        // Populate level/type dropdowns from Revit if available
        PopulateDropdownOptions(vm);

        var dialog = new RunWorkflowDialog(vm);
        dialog.Title = $"Run {toolName}";
        dialog.Topmost = true;
        if (dialog.ShowDialog() == true && dialog.ResultArgs != null)
        {
            StatusText = $"Running {toolName} ({vm.SelectedMode})...";
            await ExecuteToolRunAsync(listener, toolName, dialog.ResultArgs, vm.SelectedMode);
        }
        else
        {
            StatusText = string.Empty;
        }
    }

    private async Task ExecuteToolRunAsync(PipeListener listener, string toolName, Dictionary<string, object?> inputArgs, string executionMode = "headless")
    {
        var tcs = new TaskCompletionSource<bool>();

        void OnResponse(JsonElement payload)
        {
            _dispatcher.Invoke(() =>
            {
                var success = payload.TryGetProperty("success", out var s) && s.GetBoolean();
                if (success)
                {
                    StatusText = $"'{toolName}' completed successfully";
                }
                else
                {
                    var error = payload.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
                    var errorCode = payload.TryGetProperty("error_code", out var ec) ? ec.GetString() ?? "" : "";

                    // Show only a concise summary — full details in History tab
                    var firstLine = error.Split('\n')[0].Trim();
                    if (firstLine.Length > 100)
                        firstLine = firstLine[..97] + "...";

                    var summary = !string.IsNullOrEmpty(errorCode) ? $"[{errorCode}] {firstLine}" : firstLine;
                    StatusText = $"'{toolName}' failed: {summary}  (see History for details)";
                }
                tcs.TrySetResult(success);
            });
        }

        listener.OnToolRunResponse += OnResponse;
        try
        {
            await listener.RunToolAsync(toolName, inputArgs, executionMode);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            cts.Token.Register(() => tcs.TrySetCanceled());

            try
            {
                await tcs.Task;
            }
            catch (TaskCanceledException)
            {
                StatusText = $"'{toolName}' timed out";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            listener.OnToolRunResponse -= OnResponse;
        }
    }

    private static void PopulateDropdownOptions(RunWorkflowViewModel vm)
    {
        // Try to query Revit for levels and types via the API thread
        try
        {
            App.Instance?.RunOnRevitThread(uiApp =>
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null) return;

                foreach (var param in vm.Parameters)
                {
                    if (param.UiHint == "level_dropdown")
                    {
                        var levels = new Autodesk.Revit.DB.FilteredElementCollector(doc)
                            .OfClass(typeof(Autodesk.Revit.DB.Level))
                            .Cast<Autodesk.Revit.DB.Level>()
                            .OrderBy(l => l.Elevation)
                            .Select(l => l.Name)
                            .ToList();
                        foreach (var name in levels)
                            param.Options.Add(name);
                    }
                    else if (param.UiHint == "type_dropdown")
                    {
                        var types = new Autodesk.Revit.DB.FilteredElementCollector(doc)
                            .OfClass(typeof(Autodesk.Revit.DB.WallType))
                            .Cast<Autodesk.Revit.DB.WallType>()
                            .OrderBy(t => t.Name)
                            .Select(t => t.Name)
                            .ToList();
                        foreach (var name in types)
                            param.Options.Add(name);
                    }
                }
            });
        }
        catch
        {
            // If Revit API is not available (e.g. no document open), options stay empty
        }
    }

    private void OnToolListResponse(JsonElement payload)
    {
        _dispatcher.Invoke(() =>
        {
            // Unwire event handlers from old tools before clearing
            foreach (var old in Tools)
                old.PropertyChanged -= OnToolIsSelectedChanged;
            Tools.Clear();
            if (payload.TryGetProperty("tools", out var toolsArray))
            {
                foreach (var item in toolsArray.EnumerateArray())
                {
                    var toolInfo = new ToolInfo
                    {
                        Name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        Description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                        Adapter = item.TryGetProperty("adapter", out var a) ? a.GetString() ?? "" : "",
                        ParameterCount = item.TryGetProperty("parameter_count", out var p) ? p.GetInt32() : 0,
                        Visibility = item.TryGetProperty("visibility", out var vis) ? vis.GetString() ?? "user" : "user",
                    };

                    // Parse execution modes
                    if (item.TryGetProperty("execution", out var execEl))
                    {
                        if (execEl.TryGetProperty("modes", out var modesEl))
                        {
                            toolInfo.ExecutionModes = modesEl.EnumerateArray()
                                .Select(m => m.GetString() ?? "headless")
                                .ToList();
                        }
                        toolInfo.InteractiveHint = execEl.TryGetProperty("interactive_hint", out var hintEl)
                            ? hintEl.GetString() : null;
                    }

                    // Parse side effects, permissions, version, author, tags
                    if (item.TryGetProperty("side_effects", out var seEl))
                    {
                        toolInfo.SideEffects = seEl.EnumerateArray()
                            .Select(e => e.GetString() ?? "")
                            .ToList();
                    }
                    if (item.TryGetProperty("permissions", out var permEl))
                    {
                        toolInfo.PermissionMode = permEl.TryGetProperty("mode", out var pmEl)
                            ? pmEl.GetString() ?? "write" : "write";
                        toolInfo.ApprovalRequired = permEl.TryGetProperty("approval_required", out var arEl)
                            && arEl.GetBoolean();
                    }
                    if (item.TryGetProperty("version", out var verEl))
                        toolInfo.Version = verEl.GetString();
                    if (item.TryGetProperty("author", out var authEl))
                        toolInfo.Author = authEl.GetString();
                    if (item.TryGetProperty("tags", out var tagsEl))
                    {
                        toolInfo.Tags = tagsEl.EnumerateArray()
                            .Select(t => t.GetString() ?? "")
                            .ToList();
                    }

                    // Parse parameter schema from the tool list response
                    if (item.TryGetProperty("parameters", out var paramsEl)
                        && paramsEl.TryGetProperty("properties", out var propsEl)
                        && propsEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in propsEl.EnumerateObject())
                        {
                            var pp = new PromotedParameter
                            {
                                Type = prop.Value.TryGetProperty("type", out var t) ? t.GetString() ?? "string" : "string",
                                Description = prop.Value.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                            };
                            if (prop.Value.TryGetProperty("default", out var defVal))
                            {
                                pp.DefaultValue = GetJsonValue(defVal);
                            }
                            toolInfo.ParameterSchema[prop.Name] = pp;
                        }
                    }

                    toolInfo.PropertyChanged += OnToolIsSelectedChanged;
                    Tools.Add(toolInfo);
                }
            }
            IsLoading = false;
            _cachedTools = Tools.ToList();
            ApplyFavorites();
            ApplyFilter();
        });
    }

    private async void OnEditTool()
    {
        if (SelectedTool is null)
            return;

        if (App.Instance?.PipeListener is not { } listener || !listener.IsConnected)
        {
            StatusText = "Not connected to server";
            return;
        }

        if (SelectedTool.Adapter == "workflow")
        {
            StatusText = "Loading workflow...";
            try
            {
                await listener.LoadWorkflowAsync(SelectedTool.Name);
            }
            catch (Exception ex)
            {
                StatusText = $"Error: {ex.Message}";
            }
        }
        else
        {
            StatusText = "Loading tool definition...";
            try
            {
                await listener.LoadToolAsync(SelectedTool.Name);
            }
            catch (Exception ex)
            {
                StatusText = $"Error: {ex.Message}";
            }
        }
    }

    private void OnWorkflowLoadResponse(JsonElement payload)
    {
        _dispatcher.Invoke(async () =>
        {
            var success = payload.TryGetProperty("success", out var s) && s.GetBoolean();
            if (!success)
            {
                var error = payload.TryGetProperty("error", out var e) ? e.GetString() ?? "Unknown error" : "Unknown error";
                StatusText = $"Failed to load: {error}";
                return;
            }

            if (!payload.TryGetProperty("workflow", out var wfEl))
            {
                StatusText = "No workflow data returned";
                return;
            }

            // Parse loaded workflow into a WorkflowDefinition
            var def = ParseWorkflowFromJson(wfEl);
            def.OriginalName = def.Name;

            var vm = new WorkflowEditorViewModel(def);

            // Mark steps that reference other workflow tools
            if (_cachedTools is { Count: > 0 })
            {
                var workflowNames = new HashSet<string>(
                    _cachedTools.Where(t => t.Adapter == "workflow").Select(t => t.Name),
                    StringComparer.Ordinal);
                foreach (var step in vm.Steps)
                {
                    if (workflowNames.Contains(step.ToolName))
                        step.IsWorkflowTool = true;
                }
            }

            var resultDef = await WorkflowEditorDialog.ShowWithTestSupportAsync(vm);
            if (resultDef != null)
            {
                await SaveEditedWorkflowAsync(resultDef);
            }

            StatusText = string.Empty;
        });
    }

    private async Task SaveEditedWorkflowAsync(Models.WorkflowDefinition definition)
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
            StatusText = $"Saved: {definition.Name}";
            RequestRefresh();
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    private void OnToolLoadResponse(JsonElement payload)
    {
        _dispatcher.Invoke(async () =>
        {
            var success = payload.TryGetProperty("success", out var s) && s.GetBoolean();
            if (!success)
            {
                var error = payload.TryGetProperty("error", out var e) ? e.GetString() ?? "Unknown error" : "Unknown error";
                StatusText = $"Failed to load: {error}";
                return;
            }

            if (!payload.TryGetProperty("tool", out var toolEl) || toolEl.ValueKind != JsonValueKind.Object)
            {
                StatusText = "No tool data returned";
                return;
            }

            var vm = ToolEditorViewModel.FromJsonElement(toolEl);
            var dialog = new ToolEditorDialog(vm);
            dialog.Topmost = true;

            if (dialog.ShowDialog() == true && dialog.ResultDefinition != null)
            {
                StatusText = "Saving tool definition...";
                if (App.Instance?.PipeListener is { } listener && listener.IsConnected)
                {
                    // Pass the original name as the lookup key — the server
                    // detects a rename (vm.ToolName != vm.OriginalName) and
                    // migrates the JSON file + registry entry.
                    await listener.UpdateToolAsync(vm.OriginalName, dialog.ResultDefinition);
                }
                else
                {
                    StatusText = "Not connected to server";
                }
            }
            else
            {
                StatusText = string.Empty;
            }
        });
    }

    private void OnToolUpdateResponse(JsonElement payload)
    {
        _dispatcher.Invoke(() =>
        {
            var success = payload.TryGetProperty("success", out var s) && s.GetBoolean();
            if (success)
            {
                var toolName = payload.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "" : "";
                StatusText = $"Saved: {toolName}";
                RequestRefresh();
            }
            else
            {
                var error = payload.TryGetProperty("error", out var e) ? e.GetString() ?? "Unknown error" : "Unknown error";
                StatusText = $"Save failed: {error}";
            }
        });
    }

    private static Models.WorkflowDefinition ParseWorkflowFromJson(JsonElement wfEl)
    {
        var def = new Models.WorkflowDefinition
        {
            Name = wfEl.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            Description = wfEl.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
        };

        // Parse parameters (promoted workflow-level inputs)
        if (wfEl.TryGetProperty("parameters", out var paramsEl)
            && paramsEl.TryGetProperty("properties", out var propsEl)
            && propsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in propsEl.EnumerateObject())
            {
                var pp = new Models.PromotedParameter
                {
                    Type = prop.Value.TryGetProperty("type", out var t) ? t.GetString() ?? "string" : "string",
                    Description = prop.Value.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                };
                if (prop.Value.TryGetProperty("default", out var defVal))
                {
                    pp.DefaultValue = GetJsonValue(defVal);
                }
                def.Parameters[prop.Name] = pp;
            }
        }

        // Parse governance
        if (wfEl.TryGetProperty("governance", out var gov))
        {
            def.Author = gov.TryGetProperty("author", out var a) ? a.GetString() : null;
            def.Version = gov.TryGetProperty("version", out var v) ? v.GetString() ?? "1.0.0" : "1.0.0";
            def.PermissionMode = gov.TryGetProperty("permission_mode", out var pm) ? pm.GetString() ?? "write" : "write";
            def.ApprovalRequired = gov.TryGetProperty("approval_required", out var ar) && ar.GetBoolean();

            if (gov.TryGetProperty("tags", out var tags))
            {
                foreach (var tag in tags.EnumerateArray())
                    def.Tags.Add(tag.GetString() ?? "");
            }
            if (gov.TryGetProperty("side_effects", out var se))
            {
                foreach (var effect in se.EnumerateArray())
                    def.SideEffects.Add(effect.GetString() ?? "");
            }
        }

        // Parse steps
        if (wfEl.TryGetProperty("steps", out var stepsEl))
        {
            foreach (var stepEl in stepsEl.EnumerateArray())
            {
                var step = new Pipe.WorkflowStep
                {
                    Id = stepEl.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    ToolName = stepEl.TryGetProperty("tool", out var tool) ? tool.GetString() ?? "" : "",
                    OnFailure = stepEl.TryGetProperty("on_failure", out var of) ? of.GetString() ?? "stop" : "stop",
                    MaxRetries = stepEl.TryGetProperty("max_retries", out var mr) ? mr.GetInt32() : 0,
                    TimeoutMs = stepEl.TryGetProperty("timeout_ms", out var tm) ? tm.GetInt32() : null,
                    Guard = stepEl.TryGetProperty("guard", out var g) ? g.GetString() : null,
                };

                // Parse args
                if (stepEl.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in argsEl.EnumerateObject())
                    {
                        step.Args[prop.Name] = GetJsonValue(prop.Value);
                    }
                }

                // Parse bindings
                if (stepEl.TryGetProperty("bindings", out var bindEl) && bindEl.ValueKind == JsonValueKind.Object)
                {
                    step.Bindings = new Dictionary<string, string>();
                    foreach (var prop in bindEl.EnumerateObject())
                    {
                        step.Bindings[prop.Name] = prop.Value.GetString() ?? "";
                    }
                }

                // Parse snapshot metadata
                if (stepEl.TryGetProperty("snapshot", out var snapEl) && snapEl.ValueKind == JsonValueKind.Object)
                {
                    step.Snapshot = new Dictionary<string, object?>();
                    foreach (var prop in snapEl.EnumerateObject())
                        step.Snapshot[prop.Name] = GetJsonValue(prop.Value);

                    step.SourceType = snapEl.TryGetProperty("source_type", out var stEl)
                        ? stEl.GetString() ?? "tool" : "tool";
                    step.SourceAdapter = snapEl.TryGetProperty("adapter", out var saEl)
                        ? saEl.GetString() ?? "" : "";
                }
                else
                {
                    step.SourceType = step.ToolName.StartsWith("recorded.") ? "recording" : "tool";
                    step.SourceAdapter = "";
                }

                def.Steps.Add(step);
            }
        }

        // Parse LLM review metadata
        if (wfEl.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("llm_review", out var review))
        {
            def.WasLlmReviewed = true;
            def.LlmReviewSummary = review.TryGetProperty("summary", out var rs) ? rs.GetString() : null;
            if (review.TryGetProperty("flags", out var flags))
            {
                foreach (var flag in flags.EnumerateArray())
                    def.LlmReviewFlags.Add(flag.GetString() ?? "");
            }
        }

        // Parse derived contract
        if (wfEl.TryGetProperty("derived_contract", out var derived))
        {
            if (derived.TryGetProperty("side_effects", out var dse))
                foreach (var se in dse.EnumerateArray())
                    def.DerivedSideEffects.Add(se.GetString() ?? "");
            def.DerivedPermissionMode = derived.TryGetProperty("permission_mode", out var dpm)
                ? dpm.GetString() : null;
            if (derived.TryGetProperty("preconditions", out var dpc))
                foreach (var pc in dpc.EnumerateArray())
                    if (pc.TryGetProperty("check", out var chk))
                        def.DerivedPreconditions.Add(chk.GetString() ?? "");
        }

        return def;
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

    private void OnToolAddResponse(JsonElement payload)
    {
        _dispatcher.Invoke(() =>
        {
            IsLoading = false;
            var success = payload.TryGetProperty("success", out var s) && s.GetBoolean();
            if (success)
            {
                var toolName = payload.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "" : "";
                StatusText = $"Added: {toolName}";
                // Refresh the list to show the new tool
                RequestRefresh();
            }
            else
            {
                var error = payload.TryGetProperty("error", out var e) ? e.GetString() ?? "Unknown error" : "Unknown error";
                StatusText = $"Failed: {error}";
            }
        });
    }

    private async void OnDeleteTool()
    {
        if (SelectedTool is null)
            return;

        var toolName = SelectedTool.Name;
        var result = System.Windows.MessageBox.Show(
            $"Are you sure you want to delete '{toolName}'?\n\nThis will remove the tool definition file and cannot be undone.\n\nNote: This tool may be used as a step in existing workflows. Deleting it could break those workflows.",
            "Delete Tool",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes)
            return;

        if (App.Instance?.PipeListener is not { } listener || !listener.IsConnected)
        {
            StatusText = "Not connected to server";
            return;
        }

        IsLoading = true;
        StatusText = "Deleting tool...";
        try
        {
            await listener.DeleteToolAsync(toolName);
        }
        catch (Exception ex)
        {
            IsLoading = false;
            StatusText = $"Error: {ex.Message}";
        }
    }

    private void OnToolDeleteResponse(JsonElement payload)
    {
        _dispatcher.Invoke(() =>
        {
            IsLoading = false;
            var success = payload.TryGetProperty("success", out var s) && s.GetBoolean();
            if (success)
            {
                var toolName = payload.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "" : "";
                StatusText = $"Deleted: {toolName}";

                // Remove from favorites if present
                if (_favorites.Remove(toolName))
                    SaveFavorites();

                SelectedTool = null;
                RequestRefresh();
            }
            else
            {
                var error = payload.TryGetProperty("error", out var e) ? e.GetString() ?? "Unknown error" : "Unknown error";
                StatusText = $"Delete failed: {error}";
            }
        });
    }

    private void OnToggleComposeMode()
    {
        IsComposeMode = !IsComposeMode;
    }

    private async void OnComposeWorkflow()
    {
        var selected = Tools.Where(t => t.IsSelected).ToList();
        if (selected.Count < 2) return;

        var vm = new WorkflowEditorViewModel();

        // Default name from first tool
        var shortName = selected[0].Name;
        if (shortName.Contains('.'))
            shortName = shortName[(shortName.LastIndexOf('.') + 1)..];
        vm.WorkflowName = $"composed.{shortName}";
        vm.WorkflowDescription = $"Composed workflow: {string.Join(" → ", selected.Select(t => t.Name))}";

        foreach (var tool in selected)
        {
            var step = new WorkflowStepViewModel
            {
                ToolName = tool.Name,
                OnFailure = "stop",
                IsWorkflowTool = tool.Adapter == "workflow",
                SourceType = tool.Name.StartsWith("recorded.") ? "recording" : "tool",
                SourceAdapter = tool.Adapter,
                OnPromotionChanged = vm.RefreshPromotedParameters,
            };

            // Pre-populate args from tool's parameter schema
            foreach (var kvp in tool.ParameterSchema)
            {
                var defaultVal = kvp.Value.DefaultValue;
                var valueJson = defaultVal switch
                {
                    null => "",
                    string s => s,
                    _ => JsonSerializer.Serialize(defaultVal),
                };
                var argVm = new StepArgViewModel
                {
                    Key = kvp.Key,
                    ValueJson = valueJson,
                    IsPromoted = AutoPromotionHelper.ShouldAutoPromote(kvp.Key),
                    OnPromotionChanged = vm.RefreshPromotedParameters,
                };
                step.Args.Add(argVm);
            }

            vm.Steps.Add(step);
        }

        vm.RefreshPromotedParameters();

        var resultDef = await WorkflowEditorDialog.ShowWithTestSupportAsync(vm);
        if (resultDef != null)
        {
            await SaveEditedWorkflowAsync(resultDef);
        }

        // Exit compose mode and clear selections
        IsComposeMode = false;
    }

    private void ClearSelections()
    {
        foreach (var tool in Tools)
            tool.IsSelected = false;
        SelectedForComposeCount = 0;
    }

    private void OnToolIsSelectedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolInfo.IsSelected))
        {
            SelectedForComposeCount = Tools.Count(t => t.IsSelected);
        }
    }

    private void OnToggleFavorite(ToolInfo? tool)
    {
        if (tool is null) return;

        tool.IsFavorite = !tool.IsFavorite;

        if (tool.IsFavorite)
            _favorites.Add(tool.Name);
        else
            _favorites.Remove(tool.Name);

        SaveFavorites();
        ToolsView.Refresh();
    }

    private static HashSet<string> LoadFavorites()
    {
        try
        {
            if (File.Exists(FavoritesPath))
            {
                var json = File.ReadAllText(FavoritesPath);
                var list = JsonSerializer.Deserialize<List<string>>(json);
                if (list is not null)
                    return new HashSet<string>(list, StringComparer.Ordinal);
            }
        }
        catch
        {
            // Corrupt or unreadable file — start fresh
        }
        return new HashSet<string>(StringComparer.Ordinal);
    }

    private void SaveFavorites()
    {
        try
        {
            var dir = Path.GetDirectoryName(FavoritesPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_favorites.ToList());
            File.WriteAllText(FavoritesPath, json);
        }
        catch
        {
            // Best-effort persistence — don't crash the UI
        }
    }

    private void ApplyFavorites()
    {
        foreach (var tool in Tools)
        {
            if (_favorites.Contains(tool.Name))
                tool.IsFavorite = true;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
