using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitOrchestrator.Models;

/// <summary>
/// Envelope for all messages exchanged over the named pipe.
/// </summary>
public sealed class PipeMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("o");

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }

    public static PipeMessage Create(string type, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return new PipeMessage
        {
            Id = Guid.NewGuid().ToString(),
            Type = type,
            Timestamp = DateTimeOffset.UtcNow.ToString("o"),
            Payload = JsonDocument.Parse(json).RootElement.Clone(),
        };
    }

    public static PipeMessage Ping() => Create("ping", new { });
    public static PipeMessage Pong() => Create("pong", new { });
}
