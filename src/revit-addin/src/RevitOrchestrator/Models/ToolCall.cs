using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitOrchestrator.Models;

/// <summary>
/// Represents a tool call received from the MCP server.
/// </summary>
public sealed class ToolCall
{
    [JsonPropertyName("tool_name")]
    public string ToolName { get; set; } = string.Empty;

    [JsonPropertyName("args")]
    public JsonElement Args { get; set; }

    /// <summary>
    /// The message ID from the pipe envelope, used to correlate the response.
    /// </summary>
    [JsonIgnore]
    public string CallId { get; set; } = string.Empty;
}
