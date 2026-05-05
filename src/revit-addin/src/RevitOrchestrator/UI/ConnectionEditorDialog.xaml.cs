using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.UI;

/// <summary>
/// Modal dialog for adding or editing an MCP connection.
/// </summary>
public partial class ConnectionEditorDialog : Window
{
    private readonly McpConnectionInfo? _existing;
    private bool _suppressPresetReset;

    /// <summary>
    /// Built-in MCP server presets. Keep this list short — these are the
    /// "just works" options surfaced to non-technical users. All current
    /// presets run via npx and require Node.js.
    /// </summary>
    private static readonly Dictionary<string, McpPreset> Presets = new()
    {
        ["filesystem"] = new McpPreset(
            DefaultName: "filesystem",
            Transport:   "stdio",
            Command:     "npx",
            ArgsTemplate: "-y @modelcontextprotocol/server-filesystem",
            NeedsNode:   true,
            NeedsFolder: true,
            Hint: "Reads and writes files inside one folder. Pick the folder below — the server cannot access anything outside it. Requires Node.js."
        ),
        ["fetch"] = new McpPreset(
            DefaultName: "fetch",
            Transport:   "stdio",
            Command:     "npx",
            ArgsTemplate: "-y @modelcontextprotocol/server-fetch",
            NeedsNode:   true,
            Hint: "Fetches HTTP URLs and returns the body. Requires Node.js."
        ),
        ["memory"] = new McpPreset(
            DefaultName: "memory",
            Transport:   "stdio",
            Command:     "npx",
            ArgsTemplate: "-y @modelcontextprotocol/server-memory",
            NeedsNode:   true,
            Hint: "An in-process key/value store the LLM can scribble on. Resets when the connection stops. Requires Node.js."
        ),
        ["time"] = new McpPreset(
            DefaultName: "time",
            Transport:   "stdio",
            Command:     "npx",
            ArgsTemplate: "-y @modelcontextprotocol/server-time",
            NeedsNode:   true,
            Hint: "Returns the current time and handles timezone math. Requires Node.js."
        ),
        ["everything"] = new McpPreset(
            DefaultName: "everything",
            Transport:   "stdio",
            Command:     "npx",
            ArgsTemplate: "-y @modelcontextprotocol/server-everything",
            NeedsNode:   true,
            Hint: "Reference test server that exercises every part of the MCP protocol — useful for verifying your setup. Requires Node.js."
        ),
    };

    public ConnectionEditorDialog(McpConnectionInfo? existing = null)
    {
        _existing = existing;
        InitializeComponent();

        if (_existing != null)
        {
            Title = $"Edit Connection: {_existing.Name}";
            NameTextBox.Text = _existing.Name;
            EnabledCheckBox.IsChecked = _existing.Enabled;

            // Editing an existing connection — leave the preset on Custom so
            // we don't clobber the user's tweaks. The selection event would
            // fire from the constructor and reset fields otherwise.
            _suppressPresetReset = true;

            // Set transport
            foreach (ComboBoxItem item in TransportComboBox.Items)
            {
                if ((string)item.Tag == _existing.Transport)
                {
                    TransportComboBox.SelectedItem = item;
                    break;
                }
            }

            // Set fields
            CommandTextBox.Text = _existing.Command;
            ArgsTextBox.Text = string.Join(" ", _existing.Args);
            UrlTextBox.Text = _existing.Url;

            // Set auth type
            foreach (ComboBoxItem item in AuthTypeComboBox.Items)
            {
                if ((string)item.Tag == _existing.AuthType)
                {
                    AuthTypeComboBox.SelectedItem = item;
                    break;
                }
            }
        }
    }

    private string GetSelectedPreset()
    {
        if (PresetComboBox?.SelectedItem is ComboBoxItem item)
            return (string)item.Tag;
        return "custom";
    }

    private string GetSelectedTransport()
    {
        if (TransportComboBox.SelectedItem is ComboBoxItem item)
            return (string)item.Tag;
        return "stdio";
    }

    private string GetSelectedAuthType()
    {
        if (AuthTypeComboBox.SelectedItem is ComboBoxItem item)
            return (string)item.Tag;
        return "none";
    }

    /// <summary>
    /// Build the connection data dictionary for sending over the pipe.
    /// </summary>
    public Dictionary<string, object?> GetConnectionData()
    {
        var transport = GetSelectedTransport();
        var authType = GetSelectedAuthType();

        var data = new Dictionary<string, object?>
        {
            ["name"] = NameTextBox.Text.Trim(),
            ["transport"] = transport,
            ["enabled"] = EnabledCheckBox.IsChecked == true,
            ["command"] = CommandTextBox.Text.Trim(),
            ["args"] = ParseArgs(ArgsTextBox.Text),
            ["url"] = UrlTextBox.Text.Trim(),
            ["auth_type"] = authType,
        };

        // Encrypt credential using DPAPI before sending
        var credential = CredentialBox.Password;
        if (!string.IsNullOrEmpty(credential) && authType != "none")
        {
            var encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(credential),
                null,
                DataProtectionScope.CurrentUser);
            data["auth_credential_enc"] = Convert.ToBase64String(encrypted);
        }
        else
        {
            data["auth_credential_enc"] = "";
        }

        return data;
    }

    private void TransportComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StdioPanel is null || UrlPanel is null) return; // not yet initialized

        var transport = GetSelectedTransport();
        if (transport == "stdio")
        {
            StdioPanel.Visibility = Visibility.Visible;
            UrlPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            StdioPanel.Visibility = Visibility.Collapsed;
            UrlPanel.Visibility = Visibility.Visible;
        }
    }

    private void AuthTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CredentialLabel is null || CredentialBox is null) return;

        var authType = GetSelectedAuthType();
        if (authType == "none")
        {
            CredentialLabel.Visibility = Visibility.Collapsed;
            CredentialBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            CredentialLabel.Visibility = Visibility.Visible;
            CredentialBox.Visibility = Visibility.Visible;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Validate
        var name = NameTextBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("Connection name is required.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Filesystem preset must have a folder selected.
        if (GetSelectedPreset() == "filesystem"
            && string.IsNullOrWhiteSpace(FolderPathTextBox.Text))
        {
            MessageBox.Show("Pick a folder for the Filesystem server. The server can only access files inside that folder.",
                "Folder required",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var transport = GetSelectedTransport();
        if (transport == "stdio" && string.IsNullOrEmpty(CommandTextBox.Text.Trim()))
        {
            MessageBox.Show("Command is required for STDIO transport.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (transport != "stdio" && string.IsNullOrEmpty(UrlTextBox.Text.Trim()))
        {
            MessageBox.Show("URL is required for SSE/HTTP transport.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // ---------------------------------------------------------------------
    // Preset handling
    // ---------------------------------------------------------------------

    private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Skip the very first selection raised during InitializeComponent and
        // the one raised while we restore an existing connection's fields.
        if (NameTextBox is null || _suppressPresetReset)
        {
            _suppressPresetReset = false;
            return;
        }

        var key = GetSelectedPreset();

        if (key == "custom")
        {
            FolderPickerPanel.Visibility = Visibility.Collapsed;
            PresetHintText.Text = "Pick a preset to auto-fill the fields below, or choose Custom to configure manually.";
            return;
        }

        if (!Presets.TryGetValue(key, out var preset))
            return;

        // Apply preset to the form. The user can still edit anything afterwards.
        if (string.IsNullOrWhiteSpace(NameTextBox.Text)
            || IsKnownPresetName(NameTextBox.Text))
        {
            NameTextBox.Text = preset.DefaultName;
        }

        SelectComboBoxItem(TransportComboBox, preset.Transport);
        CommandTextBox.Text = preset.Command;
        ArgsTextBox.Text = BuildArgsForPreset(preset, FolderPathTextBox.Text);

        FolderPickerPanel.Visibility = preset.NeedsFolder ? Visibility.Visible : Visibility.Collapsed;
        PresetHintText.Text = preset.Hint;
    }

    private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Choose the folder the server can access",
        };
        if (!string.IsNullOrWhiteSpace(FolderPathTextBox.Text))
        {
            try { dlg.InitialDirectory = FolderPathTextBox.Text; } catch { }
        }

        if (dlg.ShowDialog(this) == true)
        {
            FolderPathTextBox.Text = dlg.FolderName;
        }
    }

    private void FolderPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Keep the args field in sync when the folder changes.
        var key = GetSelectedPreset();
        if (key != "custom" && Presets.TryGetValue(key, out var preset) && preset.NeedsFolder)
        {
            ArgsTextBox.Text = BuildArgsForPreset(preset, FolderPathTextBox.Text);
        }
    }

    private static string BuildArgsForPreset(McpPreset preset, string folderPath)
    {
        if (!preset.NeedsFolder || string.IsNullOrWhiteSpace(folderPath))
            return preset.ArgsTemplate;

        var path = folderPath.Trim();
        // Wrap paths that contain whitespace in double quotes so ParseArgs
        // keeps them as a single argument when the user clicks Save.
        if (path.Any(char.IsWhiteSpace) && !path.StartsWith("\""))
            path = $"\"{path}\"";

        return $"{preset.ArgsTemplate} {path}";
    }

    /// <summary>
    /// Splits the Arguments textbox into individual arguments. Whitespace
    /// separates args, but anything inside double quotes is kept as one
    /// argument (so paths with spaces work).
    /// </summary>
    internal static List<string> ParseArgs(string raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in raw)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(ch);
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }

    private static void SelectComboBoxItem(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if ((string)item.Tag == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private static bool IsKnownPresetName(string candidate)
    {
        foreach (var p in Presets.Values)
        {
            if (string.Equals(p.DefaultName, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private sealed record McpPreset(
        string DefaultName,
        string Transport,
        string Command,
        string ArgsTemplate,
        bool NeedsNode,
        string Hint,
        bool NeedsFolder = false);
}
