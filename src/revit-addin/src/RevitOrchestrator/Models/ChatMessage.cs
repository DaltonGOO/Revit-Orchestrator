namespace RevitOrchestrator.Models;

/// <summary>
/// Represents a chat message displayed in the UI.
/// </summary>
public sealed class ChatMessage
{
    public string Role { get; set; } = string.Empty; // "user", "assistant", "system"
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsToolCall { get; set; }
    public string? ToolName { get; set; }
}
