using System.Text.Json;
using System.Windows;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.UI;

/// <summary>
/// Modal dialog for reconciling type mismatches during workflow replay.
/// Shows the recorded value, available options, and lets the user pick a replacement.
/// </summary>
public partial class MappingDialog : Window
{
    public MappingResponse Result { get; private set; } = new() { Action = "cancel" };

    private readonly List<MappingOption> _options = new();

    public MappingDialog(MappingRequest request)
    {
        InitializeComponent();

        // Set header based on mapping type
        HeaderText.Text = request.MappingType switch
        {
            "system_type" => "Type Mismatch \u2014 System Type",
            "family_type" => "Type Mismatch \u2014 Family Type",
            "host_element" => "Type Mismatch \u2014 Host Element",
            _ => "Type Mismatch",
        };

        RecordedValueText.Text = request.RecordedValue;

        // Parse available options
        if (request.AvailableOptions.ValueKind == JsonValueKind.Array)
        {
            foreach (var opt in request.AvailableOptions.EnumerateArray())
            {
                var name = opt.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var typeName = opt.TryGetProperty("type_name", out var typeEl) ? typeEl.GetString() : null;
                var familyName = opt.TryGetProperty("family_name", out var famEl) ? famEl.GetString() : null;
                var classification = opt.TryGetProperty("classification", out var classEl) ? classEl.GetString() : null;
                var category = opt.TryGetProperty("category", out var catEl) ? catEl.GetString() : null;

                var displayText = name;
                if (!string.IsNullOrEmpty(typeName))
                    displayText = !string.IsNullOrEmpty(familyName) ? $"{familyName} : {typeName}" : typeName;
                if (!string.IsNullOrEmpty(classification))
                    displayText += $"  [{classification}]";
                if (!string.IsNullOrEmpty(category))
                    displayText += $"  ({category})";

                _options.Add(new MappingOption
                {
                    DisplayText = displayText,
                    RawJson = opt,
                });
            }
        }

        OptionsListBox.ItemsSource = _options;

        // Pre-select best match if available
        if (request.BestMatch.ValueKind == JsonValueKind.Object)
        {
            var bestName = request.BestMatch.TryGetProperty("name", out var bnEl) ? bnEl.GetString() : null;
            var bestType = request.BestMatch.TryGetProperty("type_name", out var btEl) ? btEl.GetString() : null;
            var matchKey = bestName ?? bestType ?? "";

            for (int i = 0; i < _options.Count; i++)
            {
                var optName = _options[i].RawJson.TryGetProperty("name", out var onEl) ? onEl.GetString() : "";
                var optType = _options[i].RawJson.TryGetProperty("type_name", out var otEl) ? otEl.GetString() : "";
                if (optName == matchKey || optType == matchKey)
                {
                    OptionsListBox.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    private void OnUseSelected(object sender, RoutedEventArgs e)
    {
        if (OptionsListBox.SelectedItem is MappingOption selected)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var prop in selected.RawJson.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? (object)l : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => prop.Value.GetRawText(),
                };
            }

            Result = new MappingResponse
            {
                Action = "map",
                SelectedOption = dict,
            };
            DialogResult = true;
            Close();
        }
        else
        {
            MessageBox.Show("Please select an option from the list.", "No Selection",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Result = new MappingResponse { Action = "cancel" };
        DialogResult = false;
        Close();
    }

    private sealed class MappingOption
    {
        public string DisplayText { get; set; } = "";
        public JsonElement RawJson { get; set; }
    }
}
