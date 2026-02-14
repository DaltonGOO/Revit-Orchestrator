using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace RevitOrchestrator.UI;

/// <summary>
/// Dialog that displays server configuration and system info.
/// Fetches settings from the Python server via the pipe.
/// </summary>
public partial class SettingsDialog : Window
{
    public SettingsDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Show Revit info from the local App context
        PopulateRevitInfo();

        // Show connection status
        var listener = App.Instance?.PipeListener;
        var isConnected = listener?.IsConnected ?? false;
        StatusDot.Fill = isConnected ? Brushes.LimeGreen : Brushes.Red;
        ConnectionStatusText.Text = isConnected ? "Connected" : "Disconnected";

        if (!isConnected || listener == null)
        {
            PipeNameText.Text = "Not connected — settings unavailable";
            return;
        }

        // Request settings from the Python server
        try
        {
            var tcs = new TaskCompletionSource<JsonElement>();
            void Handler(JsonElement payload)
            {
                tcs.TrySetResult(payload);
            }

            listener.OnSettingsResponse += Handler;
            await listener.RequestSettingsAsync();

            // Wait with timeout
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            listener.OnSettingsResponse -= Handler;

            if (completedTask != tcs.Task)
            {
                PipeNameText.Text = "Settings request timed out";
                return;
            }

            var payload = tcs.Task.Result;
            PopulateSettings(payload);
        }
        catch (Exception ex)
        {
            PipeNameText.Text = $"Error: {ex.Message}";
        }
    }

    private void PopulateSettings(JsonElement payload)
    {
        PipeNameText.Text = GetString(payload, "pipe_name");

        // LLM
        LlmProviderText.Text = GetString(payload, "llm_provider");
        LlmModelText.Text = GetString(payload, "llm_model");

        // Storage
        EventStorePathText.Text = GetString(payload, "event_store_path");
        var sizeBytes = GetLong(payload, "event_store_size_bytes");
        EventStoreSizeText.Text = FormatBytes(sizeBytes);
        EpisodeCountText.Text = GetInt(payload, "episode_count").ToString("N0");
        EventCountText.Text = GetInt(payload, "event_count").ToString("N0");
        BlobStorePathText.Text = GetString(payload, "blob_store_dir");
        BlobCountText.Text = GetInt(payload, "blob_count").ToString("N0");
        AuditLogDirText.Text = GetString(payload, "audit_log_dir");

        // Tools
        ToolsDirText.Text = GetString(payload, "tools_dir");
        ToolCountText.Text = GetInt(payload, "tool_count").ToString();
        HotReloadText.Text = GetBool(payload, "watch_tools_dir") ? "Enabled" : "Disabled";

        // ML
        EmbeddingModelText.Text = GetString(payload, "embedding_model");
        var mlAvailable = GetBool(payload, "ml_available");
        MlAvailableText.Text = mlAvailable ? "Yes" : "No (install [ml] extras)";
        MlAvailableText.Foreground = mlAvailable ? Brushes.Green : new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
    }

    private void PopulateRevitInfo()
    {
        var uiApp = App.Instance?.UiApplication;
        if (uiApp == null)
        {
            RevitVersionText.Text = "Not available";
            RevitUserText.Text = "Not available";
            RevitDocumentText.Text = "Not available";
            return;
        }

        RevitVersionText.Text = uiApp.Application.VersionNumber;
        RevitUserText.Text = uiApp.Application.Username;

        var doc = uiApp.ActiveUIDocument?.Document;
        RevitDocumentText.Text = doc?.Title ?? "No document open";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB" };
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < units.Length - 1)
        {
            size /= 1024;
            i++;
        }
        return $"{size:F1} {units[i]}";
    }

    private static string GetString(JsonElement el, string key)
    {
        if (el.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString() ?? "--";
        return "--";
    }

    private static int GetInt(JsonElement el, string key)
    {
        if (el.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.Number)
            return val.GetInt32();
        return 0;
    }

    private static long GetLong(JsonElement el, string key)
    {
        if (el.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.Number)
            return val.GetInt64();
        return 0;
    }

    private static bool GetBool(JsonElement el, string key)
    {
        if (el.TryGetProperty(key, out var val))
        {
            if (val.ValueKind == JsonValueKind.True) return true;
            if (val.ValueKind == JsonValueKind.False) return false;
        }
        return false;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
