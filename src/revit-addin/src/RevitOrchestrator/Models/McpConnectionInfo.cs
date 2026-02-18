using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace RevitOrchestrator.Models;

/// <summary>
/// Represents a managed MCP external connection displayed in the Connections tab.
/// </summary>
public sealed class McpConnectionInfo : INotifyPropertyChanged
{
    private string _status = "disconnected";
    private string? _lastError;
    private int _discoveredToolCount;
    private bool _enabled = true;

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Transport { get; set; } = "stdio";
    public string Command { get; set; } = string.Empty;
    public List<string> Args { get; set; } = new();
    public string Url { get; set; } = string.Empty;
    public string AuthType { get; set; } = "none";
    public string ServerName { get; set; } = string.Empty;
    public string ServerVersion { get; set; } = string.Empty;
    public List<string> DiscoveredToolNames { get; set; } = new();
    public string? LastConnectedAt { get; set; }
    public string? CreatedAt { get; set; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            OnPropertyChanged();
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(StatusIcon));
        }
    }

    public int DiscoveredToolCount
    {
        get => _discoveredToolCount;
        set
        {
            if (_discoveredToolCount == value) return;
            _discoveredToolCount = value;
            OnPropertyChanged();
        }
    }

    public string? LastError
    {
        get => _lastError;
        set
        {
            if (_lastError == value) return;
            _lastError = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Status dot color for UI binding.</summary>
    public SolidColorBrush StatusColor => Status switch
    {
        "connected" => new SolidColorBrush(Colors.Green),
        "connecting" => new SolidColorBrush(Colors.Orange),
        "error" => new SolidColorBrush(Colors.Red),
        _ => new SolidColorBrush(Colors.Gray),
    };

    /// <summary>Status icon for UI.</summary>
    public string StatusIcon => Status switch
    {
        "connected" => "\u2713",   // checkmark
        "connecting" => "\u23F3",  // hourglass
        "error" => "\u2717",      // cross
        _ => "\u25CB",            // circle
    };

    /// <summary>Transport badge text.</summary>
    public string TransportBadge => Transport switch
    {
        "stdio" => "STDIO",
        "sse" => "SSE",
        "streamable_http" => "HTTP",
        _ => Transport.ToUpper(),
    };

    /// <summary>Short display of server info.</summary>
    public string ServerInfo => string.IsNullOrEmpty(ServerName)
        ? ""
        : string.IsNullOrEmpty(ServerVersion)
            ? ServerName
            : $"{ServerName} v{ServerVersion}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
