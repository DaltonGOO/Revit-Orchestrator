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

    public ConnectionEditorDialog(McpConnectionInfo? existing = null)
    {
        _existing = existing;
        InitializeComponent();

        if (_existing != null)
        {
            Title = $"Edit Connection: {_existing.Name}";
            NameTextBox.Text = _existing.Name;
            EnabledCheckBox.IsChecked = _existing.Enabled;

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
            ["args"] = ArgsTextBox.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
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
}
