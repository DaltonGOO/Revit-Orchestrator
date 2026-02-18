using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RevitOrchestrator.Settings;

/// <summary>
/// Persisted LLM settings with DPAPI-encrypted API key storage.
/// </summary>
public record LlmSettings
{
    public string Provider { get; init; } = "claude";
    public string Model { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public string BaseUrl { get; init; } = "";
}

/// <summary>
/// Static helper for loading/saving LLM settings to %LOCALAPPDATA%/RevitOrchestrator/ui-settings.json.
/// API keys are encrypted with Windows DPAPI (CurrentUser scope).
/// </summary>
public static class SettingsStore
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RevitOrchestrator");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "ui-settings.json");

    public static LlmSettings Load()
    {
        if (!File.Exists(SettingsPath))
            return new LlmSettings();

        try
        {
            var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var provider = root.TryGetProperty("provider", out var p) ? p.GetString() ?? "claude" : "claude";
            var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
            var baseUrl = root.TryGetProperty("base_url", out var b) ? b.GetString() ?? "" : "";

            // Decrypt API key
            string apiKey = "";
            if (root.TryGetProperty("api_key_protected", out var enc) &&
                enc.ValueKind == JsonValueKind.String)
            {
                var encryptedBytes = Convert.FromBase64String(enc.GetString()!);
                var decryptedBytes = ProtectedData.Unprotect(
                    encryptedBytes, null, DataProtectionScope.CurrentUser);
                apiKey = Encoding.UTF8.GetString(decryptedBytes);
            }

            return new LlmSettings
            {
                Provider = provider,
                Model = model,
                ApiKey = apiKey,
                BaseUrl = baseUrl,
            };
        }
        catch
        {
            return new LlmSettings();
        }
    }

    public static void Save(LlmSettings settings)
    {
        Directory.CreateDirectory(SettingsDir);

        // Encrypt API key with DPAPI
        string encryptedKey = "";
        if (!string.IsNullOrEmpty(settings.ApiKey))
        {
            var plainBytes = Encoding.UTF8.GetBytes(settings.ApiKey);
            var encryptedBytes = ProtectedData.Protect(
                plainBytes, null, DataProtectionScope.CurrentUser);
            encryptedKey = Convert.ToBase64String(encryptedBytes);
        }

        var data = new Dictionary<string, string>
        {
            ["provider"] = settings.Provider,
            ["model"] = settings.Model,
            ["api_key_protected"] = encryptedKey,
            ["base_url"] = settings.BaseUrl,
        };

        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json, Encoding.UTF8);
    }
}
