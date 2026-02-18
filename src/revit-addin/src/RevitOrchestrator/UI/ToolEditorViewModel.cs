using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.UI;

/// <summary>
/// ViewModel for the ToolEditorDialog — edits any tool's contract.
/// </summary>
public sealed class ToolEditorViewModel : INotifyPropertyChanged
{
    private ParameterDefinition? _selectedParameter;
    private string _statusText = string.Empty;

    public ToolEditorViewModel()
    {
        AddParameterCommand = new RelayCommand(OnAddParameter);
        RemoveParameterCommand = new RelayCommand(OnRemoveParameter, () => SelectedParameter != null);
    }

    // General metadata
    public string ToolName { get; set; } = "";
    public string Adapter { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Version { get; set; }
    public string? Author { get; set; }
    public string TagsText { get; set; } = "";
    public string Visibility { get; set; } = "user";
    public static string[] AvailableVisibilities { get; } = { "user", "llm-only", "internal" };

    // Execution modes
    public bool SupportsHeadless { get; set; } = true;
    public bool SupportsInteractive { get; set; }
    public string InteractiveHint { get; set; } = "";

    // Side effects
    public bool CreatesElements { get; set; }
    public bool ModifiesElements { get; set; }
    public bool DeletesElements { get; set; }
    public bool ModifiesParameters { get; set; }
    public bool ChangesView { get; set; }
    public bool FileIo { get; set; }

    // Permissions
    public string PermissionMode { get; set; } = "write";
    public bool ApprovalRequired { get; set; }

    // Parameters
    public ObservableCollection<ParameterDefinition> Parameters { get; } = new();

    public ParameterDefinition? SelectedParameter
    {
        get => _selectedParameter;
        set
        {
            _selectedParameter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedParameter));
            (RemoveParameterCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelectedParameter => _selectedParameter != null;

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public ICommand AddParameterCommand { get; }
    public ICommand RemoveParameterCommand { get; }

    // Available types for the ComboBox
    public static string[] AvailableTypes { get; } = { "string", "integer", "number", "boolean", "array", "object" };
    public static string[] AvailablePermissionModes { get; } = { "read", "write" };

    /// <summary>
    /// Create a ViewModel from a full tool definition JSON element.
    /// </summary>
    public static ToolEditorViewModel FromJsonElement(JsonElement definition)
    {
        var vm = new ToolEditorViewModel
        {
            ToolName = definition.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            Adapter = definition.TryGetProperty("adapter", out var a) ? a.GetString() ?? "" : "",
            Description = definition.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
            Version = definition.TryGetProperty("version", out var v) ? v.GetString() : null,
            Author = definition.TryGetProperty("author", out var auth) ? auth.GetString() : null,
            Visibility = definition.TryGetProperty("visibility", out var visEl)
                ? visEl.GetString() ?? "user" : "user",
        };

        // Tags
        if (definition.TryGetProperty("tags", out var tags))
        {
            var tagList = new List<string>();
            foreach (var tag in tags.EnumerateArray())
                tagList.Add(tag.GetString() ?? "");
            vm.TagsText = string.Join(", ", tagList);
        }

        // Execution modes
        if (definition.TryGetProperty("execution", out var exec))
        {
            vm.SupportsHeadless = false;
            vm.SupportsInteractive = false;
            if (exec.TryGetProperty("modes", out var modes))
            {
                foreach (var mode in modes.EnumerateArray())
                {
                    var modeStr = mode.GetString();
                    if (modeStr == "headless") vm.SupportsHeadless = true;
                    if (modeStr == "interactive") vm.SupportsInteractive = true;
                }
            }
            if (!vm.SupportsHeadless && !vm.SupportsInteractive)
                vm.SupportsHeadless = true;

            vm.InteractiveHint = exec.TryGetProperty("interactive_hint", out var hint)
                ? hint.GetString() ?? ""
                : "";
        }

        // Side effects
        if (definition.TryGetProperty("side_effects", out var effects))
        {
            foreach (var effect in effects.EnumerateArray())
            {
                switch (effect.GetString())
                {
                    case "creates_elements": vm.CreatesElements = true; break;
                    case "modifies_elements": vm.ModifiesElements = true; break;
                    case "deletes_elements": vm.DeletesElements = true; break;
                    case "modifies_parameters": vm.ModifiesParameters = true; break;
                    case "changes_view": vm.ChangesView = true; break;
                    case "file_io": vm.FileIo = true; break;
                }
            }
        }

        // Permissions
        if (definition.TryGetProperty("permissions", out var perms))
        {
            vm.PermissionMode = perms.TryGetProperty("mode", out var pm)
                ? pm.GetString() ?? "write"
                : "write";
            vm.ApprovalRequired = perms.TryGetProperty("approval_required", out var ar) && ar.GetBoolean();
        }

        // Parameters
        if (definition.TryGetProperty("parameters", out var paramsEl)
            && paramsEl.TryGetProperty("properties", out var propsEl)
            && propsEl.ValueKind == JsonValueKind.Object)
        {
            var requiredList = new HashSet<string>();
            if (paramsEl.TryGetProperty("required", out var reqEl))
            {
                foreach (var r in reqEl.EnumerateArray())
                    requiredList.Add(r.GetString() ?? "");
            }

            foreach (var prop in propsEl.EnumerateObject())
            {
                var pd = new ParameterDefinition
                {
                    Name = prop.Name,
                    Type = prop.Value.TryGetProperty("type", out var t) ? t.GetString() ?? "string" : "string",
                    Description = prop.Value.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                    IsRequired = requiredList.Contains(prop.Name),
                };

                if (prop.Value.TryGetProperty("default", out var def))
                    pd.DefaultValue = def.ToString();

                if (prop.Value.TryGetProperty("minimum", out var min))
                    pd.Minimum = min.GetDouble();
                if (prop.Value.TryGetProperty("maximum", out var max))
                    pd.Maximum = max.GetDouble();

                if (prop.Value.TryGetProperty("enum", out var enumEl))
                {
                    var enumVals = new List<string>();
                    foreach (var e in enumEl.EnumerateArray())
                        enumVals.Add(e.ToString());
                    pd.EnumValues = string.Join(", ", enumVals);
                }

                if (prop.Value.TryGetProperty("minItems", out var minI))
                    pd.MinItems = minI.GetInt32();
                if (prop.Value.TryGetProperty("maxItems", out var maxI))
                    pd.MaxItems = maxI.GetInt32();

                vm.Parameters.Add(pd);
            }
        }

        return vm;
    }

    /// <summary>
    /// Serialize the ViewModel state back into a tool definition dictionary.
    /// </summary>
    public Dictionary<string, object?> ToDefinition()
    {
        var definition = new Dictionary<string, object?>
        {
            ["name"] = ToolName,
            ["adapter"] = Adapter,
            ["description"] = Description,
            ["visibility"] = Visibility,
        };

        // Version, author, tags
        if (!string.IsNullOrWhiteSpace(Version))
            definition["version"] = Version;
        if (!string.IsNullOrWhiteSpace(Author))
            definition["author"] = Author;

        var tagsList = string.IsNullOrWhiteSpace(TagsText)
            ? new List<string>()
            : TagsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (tagsList.Count > 0)
            definition["tags"] = tagsList;

        // Parameters
        var properties = new Dictionary<string, object?>();
        var required = new List<string>();

        foreach (var p in Parameters)
        {
            var propDef = new Dictionary<string, object?>
            {
                ["type"] = p.Type,
            };
            if (!string.IsNullOrWhiteSpace(p.Description))
                propDef["description"] = p.Description;
            if (!string.IsNullOrWhiteSpace(p.DefaultValue))
                propDef["default"] = ParseDefaultValue(p.DefaultValue, p.Type);

            // Constraints
            if (p.IsNumericType)
            {
                if (p.Minimum.HasValue)
                    propDef["minimum"] = p.Minimum.Value;
                if (p.Maximum.HasValue)
                    propDef["maximum"] = p.Maximum.Value;
            }
            if (p.IsStringType && !string.IsNullOrWhiteSpace(p.EnumValues))
            {
                var enumVals = p.EnumValues.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                propDef["enum"] = enumVals;
            }
            if (p.IsArrayType)
            {
                if (p.MinItems.HasValue)
                    propDef["minItems"] = p.MinItems.Value;
                if (p.MaxItems.HasValue)
                    propDef["maxItems"] = p.MaxItems.Value;
            }

            properties[p.Name] = propDef;
            if (p.IsRequired)
                required.Add(p.Name);
        }

        definition["parameters"] = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        };

        // Execution modes
        var modes = new List<string>();
        if (SupportsHeadless) modes.Add("headless");
        if (SupportsInteractive) modes.Add("interactive");
        if (modes.Count == 0) modes.Add("headless");

        if (SupportsInteractive || modes.Count > 1)
        {
            var execution = new Dictionary<string, object?> { ["modes"] = modes };
            if (!string.IsNullOrWhiteSpace(InteractiveHint))
                execution["interactive_hint"] = InteractiveHint;
            definition["execution"] = execution;
        }

        // Side effects
        var sideEffects = new List<string>();
        if (CreatesElements) sideEffects.Add("creates_elements");
        if (ModifiesElements) sideEffects.Add("modifies_elements");
        if (DeletesElements) sideEffects.Add("deletes_elements");
        if (ModifiesParameters) sideEffects.Add("modifies_parameters");
        if (ChangesView) sideEffects.Add("changes_view");
        if (FileIo) sideEffects.Add("file_io");
        if (sideEffects.Count > 0)
            definition["side_effects"] = sideEffects;

        // Permissions
        var permissions = new Dictionary<string, object?>
        {
            ["mode"] = PermissionMode,
        };
        if (ApprovalRequired)
            permissions["approval_required"] = true;
        definition["permissions"] = permissions;

        return definition;
    }

    private void OnAddParameter()
    {
        var param = new ParameterDefinition
        {
            Name = $"param_{Parameters.Count + 1}",
            Type = "string",
        };
        Parameters.Add(param);
        SelectedParameter = param;
    }

    private void OnRemoveParameter()
    {
        if (SelectedParameter is null) return;
        var idx = Parameters.IndexOf(SelectedParameter);
        Parameters.Remove(SelectedParameter);
        if (Parameters.Count > 0)
            SelectedParameter = Parameters[Math.Min(idx, Parameters.Count - 1)];
        else
            SelectedParameter = null;
    }

    private static object? ParseDefaultValue(string value, string type)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return type switch
        {
            "integer" => long.TryParse(value, out var l) ? l : (object)value,
            "number" => double.TryParse(value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : (object)value,
            "boolean" => bool.TryParse(value, out var b) ? b : (object)value,
            "array" or "object" => TryParseJson(value) ?? value,
            _ => value,
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
            return null;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
