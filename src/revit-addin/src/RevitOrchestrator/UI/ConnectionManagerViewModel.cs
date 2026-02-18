using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using RevitOrchestrator.Models;
using RevitOrchestrator.Pipe;

namespace RevitOrchestrator.UI;

/// <summary>
/// MVVM ViewModel for the Connections tab.
/// </summary>
public sealed class ConnectionManagerViewModel : INotifyPropertyChanged
{
    private bool _isLoading;
    private McpConnectionInfo? _selectedConnection;
    private string _statusText = string.Empty;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public ConnectionManagerViewModel()
    {
        RefreshCommand = new RelayCommand(OnRefresh);
        AddConnectionCommand = new RelayCommand(OnAddConnection);
        EditConnectionCommand = new RelayCommand(OnEditConnection, () => SelectedConnection != null);
        DeleteConnectionCommand = new RelayCommand(OnDeleteConnection, () => SelectedConnection != null);
        TestConnectionCommand = new RelayCommand(OnTestConnection, () => SelectedConnection != null);
        ToggleEnabledCommand = new RelayCommand<McpConnectionInfo>(OnToggleEnabled);

        if (App.Instance?.PipeListener is { } listener)
        {
            listener.OnConnectionListResponse += OnConnectionListResponse;
            listener.OnConnectionAddResponse += OnConnectionAddResponse;
            listener.OnConnectionUpdateResponse += OnConnectionUpdateResponse;
            listener.OnConnectionDeleteResponse += OnConnectionDeleteResponse;
            listener.OnConnectionTestResponse += OnConnectionTestResponse;
            listener.OnConnectionToggleResponse += OnConnectionToggleResponse;
            listener.OnConnectionStatusEvent += OnConnectionStatusEvent;
        }
    }

    public ObservableCollection<McpConnectionInfo> Connections { get; } = new();

    public bool IsLoading
    {
        get => _isLoading;
        set { if (_isLoading == value) return; _isLoading = value; OnPropertyChanged(); }
    }

    public McpConnectionInfo? SelectedConnection
    {
        get => _selectedConnection;
        set { if (_selectedConnection == value) return; _selectedConnection = value; OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        set { if (_statusText == value) return; _statusText = value; OnPropertyChanged(); }
    }

    public bool ShowEmptyState => !IsLoading && Connections.Count == 0;

    public ICommand RefreshCommand { get; }
    public ICommand AddConnectionCommand { get; }
    public ICommand EditConnectionCommand { get; }
    public ICommand DeleteConnectionCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand ToggleEnabledCommand { get; }

    public void RequestRefresh()
    {
        OnRefresh();
    }

    private void OnRefresh()
    {
        try
        {
            IsLoading = true;
            _ = App.Instance?.PipeListener?.RequestConnectionListAsync();
        }
        catch
        {
            IsLoading = false;
            StatusText = "Failed to refresh connections";
        }
    }

    private void OnAddConnection()
    {
        var dialog = new ConnectionEditorDialog();
        if (dialog.ShowDialog() == true)
        {
            var data = dialog.GetConnectionData();
            try
            {
                _ = App.Instance?.PipeListener?.AddConnectionAsync(data);
                StatusText = "Adding connection...";
            }
            catch
            {
                StatusText = "Failed to add connection";
            }
        }
    }

    private void OnEditConnection()
    {
        if (SelectedConnection is null) return;

        var dialog = new ConnectionEditorDialog(SelectedConnection);
        if (dialog.ShowDialog() == true)
        {
            var data = dialog.GetConnectionData();
            try
            {
                _ = App.Instance?.PipeListener?.UpdateConnectionAsync(SelectedConnection.Id, data);
                StatusText = "Updating connection...";
            }
            catch
            {
                StatusText = "Failed to update connection";
            }
        }
    }

    private void OnDeleteConnection()
    {
        if (SelectedConnection is null) return;

        var result = System.Windows.MessageBox.Show(
            $"Delete connection '{SelectedConnection.Name}'?\n\nThis will disconnect and remove all associated tools.",
            "Delete Connection",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result == System.Windows.MessageBoxResult.Yes)
        {
            try
            {
                _ = App.Instance?.PipeListener?.DeleteConnectionAsync(SelectedConnection.Id);
                StatusText = "Deleting connection...";
            }
            catch
            {
                StatusText = "Failed to delete connection";
            }
        }
    }

    private void OnTestConnection()
    {
        if (SelectedConnection is null) return;

        try
        {
            _ = App.Instance?.PipeListener?.TestConnectionAsync(SelectedConnection.Id);
            StatusText = $"Testing '{SelectedConnection.Name}'...";
        }
        catch
        {
            StatusText = "Failed to test connection";
        }
    }

    private void OnToggleEnabled(McpConnectionInfo? conn)
    {
        if (conn is null) return;

        try
        {
            _ = App.Instance?.PipeListener?.ToggleConnectionAsync(conn.Id, !conn.Enabled);
            StatusText = conn.Enabled ? $"Disabling '{conn.Name}'..." : $"Enabling '{conn.Name}'...";
        }
        catch
        {
            StatusText = "Failed to toggle connection";
        }
    }

    // ---------------------------------------------------------------
    // Event handlers
    // ---------------------------------------------------------------

    private void OnConnectionListResponse(JsonElement payload)
    {
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                IsLoading = false;
                Connections.Clear();

                if (payload.TryGetProperty("connections", out var connsEl) &&
                    connsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in connsEl.EnumerateArray())
                    {
                        Connections.Add(ParseConnection(el));
                    }
                }

                StatusText = $"{Connections.Count} connection(s)";
                OnPropertyChanged(nameof(ShowEmptyState));
            }
            catch
            {
                StatusText = "Error parsing connection list";
            }
        });
    }

    private void OnConnectionAddResponse(JsonElement payload)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var success = payload.TryGetProperty("success", out var s) && s.GetBoolean();
            if (success)
            {
                StatusText = "Connection added";
                OnRefresh();
            }
            else
            {
                var error = payload.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
                StatusText = $"Add failed: {error}";
            }
        });
    }

    private void OnConnectionUpdateResponse(JsonElement payload)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var success = payload.TryGetProperty("success", out var s) && s.GetBoolean();
            if (success)
            {
                StatusText = "Connection updated";
                OnRefresh();
            }
            else
            {
                var error = payload.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
                StatusText = $"Update failed: {error}";
            }
        });
    }

    private void OnConnectionDeleteResponse(JsonElement payload)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var success = payload.TryGetProperty("success", out var s) && s.GetBoolean();
            if (success)
            {
                StatusText = "Connection deleted";
                OnRefresh();
                // Also refresh tool list since tools were unregistered
                try { _ = App.Instance?.PipeListener?.RequestToolListAsync(); } catch { }
            }
            else
            {
                var error = payload.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
                StatusText = $"Delete failed: {error}";
            }
        });
    }

    private void OnConnectionTestResponse(JsonElement payload)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var success = payload.TryGetProperty("success", out var s) && s.GetBoolean();
            if (success)
            {
                var toolCount = 0;
                if (payload.TryGetProperty("discovered_tools", out var tools) &&
                    tools.ValueKind == JsonValueKind.Array)
                {
                    toolCount = tools.GetArrayLength();
                }
                var serverName = payload.TryGetProperty("server_name", out var sn) ? sn.GetString() : "";
                StatusText = $"Test OK: {serverName} ({toolCount} tools discovered)";
                OnRefresh();
            }
            else
            {
                var error = payload.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
                StatusText = $"Test failed: {error}";
            }
        });
    }

    private void OnConnectionToggleResponse(JsonElement payload)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var success = payload.TryGetProperty("success", out var s) && s.GetBoolean();
            if (success)
            {
                StatusText = "Connection toggled";
                OnRefresh();
                // Refresh tool list since tools may have been registered/unregistered
                try { _ = App.Instance?.PipeListener?.RequestToolListAsync(); } catch { }
            }
            else
            {
                var error = payload.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
                StatusText = $"Toggle failed: {error}";
            }
        });
    }

    private void OnConnectionStatusEvent(JsonElement payload)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (payload.TryGetProperty("connection_id", out var idEl))
            {
                var connId = idEl.GetString() ?? "";
                var conn = Connections.FirstOrDefault(c => c.Id == connId);
                if (conn != null)
                {
                    if (payload.TryGetProperty("status", out var statusEl))
                        conn.Status = statusEl.GetString() ?? "disconnected";
                    if (payload.TryGetProperty("error", out var errorEl))
                        conn.LastError = errorEl.GetString();
                }
            }
        });
    }

    // ---------------------------------------------------------------
    // Parsing
    // ---------------------------------------------------------------

    private static McpConnectionInfo ParseConnection(JsonElement el)
    {
        var conn = new McpConnectionInfo
        {
            Id = el.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            Name = el.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            Transport = el.TryGetProperty("transport", out var tr) ? tr.GetString() ?? "stdio" : "stdio",
            Enabled = el.TryGetProperty("enabled", out var en) && en.GetBoolean(),
            Command = el.TryGetProperty("command", out var cmd) ? cmd.GetString() ?? "" : "",
            Url = el.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "",
            AuthType = el.TryGetProperty("auth_type", out var auth) ? auth.GetString() ?? "none" : "none",
            Status = el.TryGetProperty("status", out var st) ? st.GetString() ?? "disconnected" : "disconnected",
            ServerName = el.TryGetProperty("server_name", out var sn) ? sn.GetString() ?? "" : "",
            ServerVersion = el.TryGetProperty("server_version", out var sv) ? sv.GetString() ?? "" : "",
            DiscoveredToolCount = el.TryGetProperty("discovered_tool_count", out var tc) ? tc.GetInt32() : 0,
            LastError = el.TryGetProperty("last_error", out var le) && le.ValueKind != JsonValueKind.Null
                ? le.GetString() : null,
            LastConnectedAt = el.TryGetProperty("last_connected_at", out var lc) && lc.ValueKind != JsonValueKind.Null
                ? lc.GetString() : null,
        };

        if (el.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
        {
            conn.Args = argsEl.EnumerateArray()
                .Select(a => a.GetString() ?? "")
                .ToList();
        }

        if (el.TryGetProperty("discovered_tool_names", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array)
        {
            conn.DiscoveredToolNames = toolsEl.EnumerateArray()
                .Select(t => t.GetString() ?? "")
                .ToList();
        }

        return conn;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
