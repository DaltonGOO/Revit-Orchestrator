using System.Text.Json.Serialization;

namespace RevitOrchestrator.Models;

/// <summary>
/// Result of executing a tool call, sent back over the pipe.
/// </summary>
public sealed class ToolResult
{
    [JsonPropertyName("call_id")]
    public string CallId { get; set; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("data")]
    public Dictionary<string, object?> Data { get; set; } = new();

    [JsonPropertyName("error")]
    public ToolError? Error { get; set; }

    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }

    public static ToolResult Ok(string callId, Dictionary<string, object?> data, long durationMs = 0)
    {
        return new ToolResult
        {
            CallId = callId,
            Success = true,
            Data = data,
            DurationMs = durationMs,
        };
    }

    public static ToolResult Fail(string callId, string code, string message, long durationMs = 0)
    {
        return new ToolResult
        {
            CallId = callId,
            Success = false,
            Error = new ToolError { Code = code, Message = message },
            DurationMs = durationMs,
        };
    }
}

public sealed class ToolError
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
