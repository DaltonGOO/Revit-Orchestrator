using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RevitOrchestrator.Settings;

namespace RevitOrchestrator.UI;

/// <summary>
/// Dialog that displays server configuration and system info.
/// Fetches settings from the Python server via the pipe.
/// LLM configuration is editable and persisted locally with DPAPI encryption.
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
        // Pre-populate editable LLM fields from local store
        var saved = SettingsStore.Load();
        SetProviderComboBox(saved.Provider);
        ModelTextBox.Text = saved.Model;
        ApiKeyBox.Password = saved.ApiKey;
        BaseUrlTextBox.Text = saved.BaseUrl;
        UpdateBaseUrlVisibility();

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

    private void SetProviderComboBox(string provider)
    {
        foreach (ComboBoxItem item in ProviderComboBox.Items)
        {
            if ((string)item.Tag == provider)
            {
                ProviderComboBox.SelectedItem = item;
                return;
            }
        }
        // Default to Claude if no match
        ProviderComboBox.SelectedIndex = 0;
    }

    private string GetSelectedProvider()
    {
        if (ProviderComboBox.SelectedItem is ComboBoxItem item)
            return (string)item.Tag;
        return "claude";
    }

    private void ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateBaseUrlVisibility();

        // Set default model hints based on provider
        var provider = GetSelectedProvider();
        if (string.IsNullOrWhiteSpace(ModelTextBox.Text) || ModelTextBox.Text == "--")
        {
            ModelTextBox.Text = provider switch
            {
                "claude" => "claude-sonnet-4-20250514",
                "openai" => "gpt-4o",
                "openai-compatible" => "llama3",
                _ => ""
            };
        }
    }

    private void UpdateBaseUrlVisibility()
    {
        if (BaseUrlLabel == null || BaseUrlTextBox == null)
            return;

        var provider = GetSelectedProvider();
        var vis = provider == "openai-compatible" ? Visibility.Visible : Visibility.Collapsed;
        BaseUrlLabel.Visibility = vis;
        BaseUrlTextBox.Visibility = vis;
    }

    private async void ApplyLlmButton_Click(object sender, RoutedEventArgs e)
    {
        var provider = GetSelectedProvider();
        var model = ModelTextBox.Text.Trim();
        var apiKey = ApiKeyBox.Password;
        var baseUrl = BaseUrlTextBox.Text.Trim();

        // Local validation
        if (string.IsNullOrEmpty(model))
        {
            ShowStatus("Model name is required.", isError: true);
            return;
        }
        if (provider is "claude" or "openai" && string.IsNullOrEmpty(apiKey))
        {
            ShowStatus("API key is required for cloud providers.", isError: true);
            return;
        }
        if (provider == "openai-compatible" && string.IsNullOrEmpty(baseUrl))
        {
            ShowStatus("Base URL is required for OpenAI-compatible providers.", isError: true);
            return;
        }

        // Save locally with DPAPI encryption
        SettingsStore.Save(new LlmSettings
        {
            Provider = provider,
            Model = model,
            ApiKey = apiKey,
            BaseUrl = baseUrl,
        });

        // Send to Python server
        var listener = App.Instance?.PipeListener;
        if (listener == null || !listener.IsConnected)
        {
            ShowStatus("Settings saved locally. Not connected to server — will apply on next connection.", isError: false);
            return;
        }

        ApplyLlmButton.IsEnabled = false;
        ShowStatus("Applying...", isError: false);

        try
        {
            var tcs = new TaskCompletionSource<JsonElement>();
            void Handler(JsonElement payload)
            {
                tcs.TrySetResult(payload);
            }

            listener.OnSettingsUpdateResponse += Handler;
            await listener.UpdateSettingsAsync(provider, apiKey, model, baseUrl);

            // Wait with timeout
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(10000));
            listener.OnSettingsUpdateResponse -= Handler;

            if (completedTask != tcs.Task)
            {
                ShowStatus("Settings update timed out.", isError: true);
                return;
            }

            var result = tcs.Task.Result;
            var success = result.TryGetProperty("success", out var s) && s.GetBoolean();
            if (success)
            {
                ShowStatus("Settings applied successfully.", isError: false);
            }
            else
            {
                var error = result.TryGetProperty("error", out var err) ? err.GetString() ?? "Unknown error" : "Unknown error";
                ShowStatus($"Failed: {error}", isError: true);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Error: {ex.Message}", isError: true);
        }
        finally
        {
            ApplyLlmButton.IsEnabled = true;
        }
    }

    private void ShowStatus(string text, bool isError)
    {
        LlmStatusText.Text = text;
        LlmStatusText.Foreground = isError
            ? Brushes.Red
            : new SolidColorBrush(Color.FromRgb(0x22, 0x8B, 0x22)); // ForestGreen
    }

    private void PopulateSettings(JsonElement payload)
    {
        PipeNameText.Text = GetString(payload, "pipe_name");

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
