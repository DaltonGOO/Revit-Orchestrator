using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using RevitOrchestrator.Pipe;

namespace RevitOrchestrator.UI;

/// <summary>
/// "Dynamo-Player" style dialog for running a Dynamo graph with user-supplied
/// inputs. Calls the Python tools <c>dynamo.describe_graph</c> (to discover
/// the inputs) and <c>dynamo.run_graph</c> (to execute the graph) over the
/// existing pipe — same backend the chat uses.
/// </summary>
public partial class RunDynamoGraphDialog : Window
{
    /// <summary>What the user did. Read by the caller after ShowDialog returns.</summary>
    public string ResultStatus { get; private set; } = "cancelled";

    /// <summary>The values the user typed in the form (when ResultStatus == "ran").</summary>
    public Dictionary<string, object?> InputsUsed { get; private set; } = new();

    /// <summary>The dynamo.run_graph result payload (when ResultStatus == "ran").</summary>
    public Dictionary<string, object?> RunResult { get; private set; } = new();

    private readonly List<InputFieldBinding> _inputFields = new();
    private readonly Dictionary<string, object?> _suggestedInputs;
    private string _graphName = "";
    private bool _busy;

    public RunDynamoGraphDialog(string? graphPath = null,
                                Dictionary<string, object?>? suggestedInputs = null)
    {
        InitializeComponent();
        _suggestedInputs = suggestedInputs ?? new();

        if (!string.IsNullOrWhiteSpace(graphPath))
        {
            GraphPathTextBox.Text = graphPath;
            // Auto-load inputs if a path was supplied
            Loaded += async (_, _) => await LoadInputsAsync();
        }
    }

    // ───────── Browse / Load ─────────

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select a Dynamo graph",
            Filter = "Dynamo graph (*.dyn)|*.dyn|All files (*.*)|*.*",
        };
        if (!string.IsNullOrWhiteSpace(GraphPathTextBox.Text))
        {
            try
            {
                var dir = Path.GetDirectoryName(GraphPathTextBox.Text);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    dlg.InitialDirectory = dir;
            }
            catch { }
        }
        if (dlg.ShowDialog(this) == true)
        {
            GraphPathTextBox.Text = dlg.FileName;
        }
    }

    private async void LoadInputsButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadInputsAsync();
    }

    private async Task LoadInputsAsync()
    {
        var path = GraphPathTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(path))
        {
            StatusText.Text = "Pick a .dyn file first.";
            return;
        }
        if (!File.Exists(path))
        {
            StatusText.Text = $"File not found: {path}";
            return;
        }

        var listener = App.Instance?.PipeListener;
        if (listener is null)
        {
            StatusText.Text = "Pipe is not connected. Is the orchestrator server running?";
            return;
        }

        SetBusy(true, "Discovering inputs…");
        InputsPanel.Children.Clear();
        _inputFields.Clear();
        RunButton.IsEnabled = false;

        try
        {
            var (success, payload, error) = await CallToolAsync(
                listener,
                "dynamo.describe_graph",
                new Dictionary<string, object?> { ["graph_path"] = path });

            if (!success)
            {
                StatusText.Text = $"Could not describe graph: {error}";
                return;
            }

            _graphName = payload.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var description = payload.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";

            var inputs = payload.TryGetProperty("inputs", out var ie) && ie.ValueKind == JsonValueKind.Array
                ? ie
                : default;

            if (inputs.ValueKind != JsonValueKind.Array || inputs.GetArrayLength() == 0)
            {
                GraphSummaryText.Text = string.IsNullOrEmpty(_graphName)
                    ? "(graph has no inputs flagged 'Is Input')"
                    : $"{_graphName} — no inputs flagged 'Is Input'.";
                AddPlaceholder("This graph has no inputs to fill in. Click Run to execute it as-is.");
                RunButton.IsEnabled = true;
                StatusText.Text = "Ready.";
                return;
            }

            GraphSummaryText.Text = string.IsNullOrEmpty(_graphName) ? description : _graphName;

            foreach (var inputEl in inputs.EnumerateArray())
            {
                var name = inputEl.TryGetProperty("name", out var ne) ? ne.GetString() ?? "" : "";
                var type = inputEl.TryGetProperty("type", out var te) ? te.GetString() ?? "string" : "string";
                var defaultVal = inputEl.TryGetProperty("default", out var de) ? de : default;
                var inputDesc = inputEl.TryGetProperty("description", out var ide) ? ide.GetString() ?? "" : "";

                if (string.IsNullOrEmpty(name)) continue;

                AddInputRow(name, type, defaultVal, inputDesc);
            }

            RunButton.IsEnabled = true;
            StatusText.Text = $"Loaded {_inputFields.Count} input(s). Edit values and click Run.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error loading inputs: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ───────── Form rendering ─────────

    private void AddPlaceholder(string text)
    {
        InputsPanel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = System.Windows.Media.Brushes.Gray,
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
    }

    private void AddInputRow(string name, string type, JsonElement defaultVal, string description)
    {
        InputsPanel.Children.Add(new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 12, 0, 4),
        });

        if (!string.IsNullOrEmpty(description))
        {
            InputsPanel.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = System.Windows.Media.Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // Pre-fill priority: suggested_inputs > default from .dyn
        object? prefill = null;
        if (_suggestedInputs.TryGetValue(name, out var s)) prefill = s;
        else if (defaultVal.ValueKind != JsonValueKind.Undefined && defaultVal.ValueKind != JsonValueKind.Null)
            prefill = JsonElementToObject(defaultVal);

        var binding = new InputFieldBinding { Name = name, Type = type };

        if (string.Equals(type, "boolean", StringComparison.OrdinalIgnoreCase))
        {
            var cb = new CheckBox
            {
                Margin = new Thickness(0, 0, 0, 4),
                IsChecked = prefill is bool bb ? bb
                          : prefill is string sb && bool.TryParse(sb, out var p) ? p
                          : false,
            };
            InputsPanel.Children.Add(cb);
            binding.Read = () => cb.IsChecked == true;
        }
        else
        {
            var tb = new TextBox
            {
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 4),
                Text = prefill?.ToString() ?? "",
            };
            InputsPanel.Children.Add(tb);
            binding.Read = () => tb.Text;
        }

        InputsPanel.Children.Add(new TextBlock
        {
            Text = $"type: {type}",
            Foreground = System.Windows.Media.Brushes.Gray,
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 0),
        });

        _inputFields.Add(binding);
    }

    // ───────── Run ─────────

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        var listener = App.Instance?.PipeListener;
        if (listener is null)
        {
            StatusText.Text = "Pipe is not connected.";
            return;
        }

        var path = GraphPathTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(path))
        {
            StatusText.Text = "Pick a graph first.";
            return;
        }

        // Collect form values, with type coercion
        var inputs = new Dictionary<string, object?>();
        foreach (var f in _inputFields)
        {
            object? raw = f.Read();
            inputs[f.Name] = CoerceUserValue(raw, f.Type);
        }

        // Fire-and-forget: send the request and close the dialog right away.
        // Dynamo runs can take minutes; staring at a "Running graph…" status
        // is worse UX than letting the user get back to work and tracking
        // progress + outcome in the History tab.
        try
        {
            var args = new Dictionary<string, object?>
            {
                ["graph_path"] = path,
                ["inputs"] = inputs,
            };
            await listener.RunToolAsync("dynamo.run_graph", args);
            ResultStatus = "ran";
            InputsUsed = inputs;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to start run: {ex.Message}";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ResultStatus = "cancelled";
        DialogResult = false;
        Close();
    }

    // ───────── Pipe helpers ─────────

    /// <summary>
    /// Send a tool_run_request and await the matching tool_run_response.
    /// Returns (success, payload, error) — payload is the data dict on success.
    /// </summary>
    private static async Task<(bool ok, JsonElement payload, string error)> CallToolAsync(
        PipeListener listener,
        string toolName,
        Dictionary<string, object?> args)
    {
        var tcs = new TaskCompletionSource<(bool, JsonElement, string)>();

        void OnResponse(JsonElement payload)
        {
            var success = payload.TryGetProperty("success", out var s) && s.GetBoolean();
            if (success)
            {
                var data = payload.TryGetProperty("data", out var d) ? d : default;
                tcs.TrySetResult((true, data, ""));
            }
            else
            {
                var error = payload.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
                var code = payload.TryGetProperty("error_code", out var ec) ? ec.GetString() ?? "" : "";
                var combined = string.IsNullOrEmpty(code) ? error : $"[{code}] {error}";
                tcs.TrySetResult((false, default, combined));
            }
        }

        listener.OnToolRunResponse += OnResponse;
        try
        {
            await listener.RunToolAsync(toolName, args);

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            cts.Token.Register(() => tcs.TrySetCanceled());

            try
            {
                return await tcs.Task;
            }
            catch (TaskCanceledException)
            {
                return (false, default, "Tool call timed out");
            }
        }
        finally
        {
            listener.OnToolRunResponse -= OnResponse;
        }
    }

    // ───────── Misc ─────────

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        if (status != null) StatusText.Text = status;
        LoadInputsButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        RunButton.IsEnabled = !busy && _inputFields.Count >= 0 && !string.IsNullOrEmpty(GraphPathTextBox.Text);
        // RunButton starts disabled until inputs are loaded; preserve that state
        if (busy) RunButton.IsEnabled = false;
    }

    private static object? CoerceUserValue(object? raw, string type)
    {
        if (raw is null) return null;
        var t = type?.Trim().ToLowerInvariant();

        switch (t)
        {
            case "integer":
                return long.TryParse(raw.ToString(), out var i) ? i : raw.ToString();
            case "number":
                return double.TryParse(raw.ToString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var d) ? d : raw.ToString();
            case "boolean":
                if (raw is bool b) return b;
                return bool.TryParse(raw.ToString(), out var pb) ? pb : raw.ToString();
            default:
                return raw.ToString();
        }
    }

    private static object? JsonElementToObject(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number =>
                el.TryGetInt64(out var i) ? (object?)i :
                el.TryGetDouble(out var d) ? d : el.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => el.GetRawText(),
        };
    }

    private static Dictionary<string, object?> JsonElementToDict(JsonElement el)
    {
        var result = new Dictionary<string, object?>();
        if (el.ValueKind != JsonValueKind.Object) return result;
        foreach (var prop in el.EnumerateObject())
            result[prop.Name] = JsonElementToObject(prop.Value);
        return result;
    }

    private sealed class InputFieldBinding
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "string";
        public Func<object?> Read { get; set; } = () => null;
    }
}
