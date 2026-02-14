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
        EditToolCommand = new RelayCommand(OnEditTool, () => SelectedTool?.Adapter == "workflow");
        EditContractCommand = new RelayCommand(OnEditContract, () => SelectedTool != null);
        RunToolCommand = new RelayCommand(OnRunTool, () => SelectedTool != null);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        ToggleFavoriteCommand = new RelayCommand<ToolInfo>(OnToggleFavorite);

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

    public ToolInfo? SelectedTool
    {
        get => _selectedTool;
        set
        {
            _selectedTool = value;
            OnPropertyChanged();
            (DeleteToolCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (EditToolCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (EditContractCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RunToolCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

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

    public ICommand RefreshCommand { get; }
    public ICommand AddToolCommand { get; }
    public ICommand DeleteToolCommand { get; }
    public ICommand EditToolCommand { get; }
    public ICommand EditContractCommand { get; }
    public ICommand RunToolCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }

    private bool FilterTool(object obj)
    {
        if (string.IsNullOrEmpty(_searchText))
            return true;

        if (obj is not ToolInfo tool)
            return false;

        return tool.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
            || tool.Description.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
            || tool.Adapter.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
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

        if (string.IsNullOrEmpty(_searchText))
        {
            StatusText = $"{Tools.Count} tool(s) registered";
        }
        else
        {
            var filtered = ToolsView.Cast<object>().Count();
            StatusText = $"{filtered} of {Tools.Count} tool(s) matching '{_searchText}'";
        }
    }

    public async void RequestRefresh()
    {
        if (App.Instance?.PipeListener is not { } listener || !listener.IsConnected)
        {
            if (_cachedTools is { Count: > 0 })
            {
                Tools.Clear();
                foreach (var tool in _cachedTools)
                    Tools.Add(tool);
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
                var error = payload.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
                StatusText = success
                    ? $"'{toolName}' completed successfully"
                    : $"'{toolName}' failed: {error}";
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
        if (SelectedTool is null || SelectedTool.Adapter != "workflow")
            return;

        if (App.Instance?.PipeListener is not { } listener || !listener.IsConnected)
        {
            StatusText = "Not connected to server";
            return;
        }

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

    private async void OnEditContract()
    {
        if (SelectedTool is null)
            return;

        if (App.Instance?.PipeListener is not { } listener || !listener.IsConnected)
        {
            StatusText = "Not connected to server";
            return;
        }

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
                    await listener.UpdateToolAsync(vm.ToolName, dialog.ResultDefinition);
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
            $"Are you sure you want to delete '{toolName}'?\n\nThis will remove the tool definition file and cannot be undone.",
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
