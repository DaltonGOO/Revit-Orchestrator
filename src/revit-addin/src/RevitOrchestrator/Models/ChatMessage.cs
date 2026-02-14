using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RevitOrchestrator.Models;

/// <summary>
/// Represents a chat message displayed in the UI.
/// </summary>
public sealed class ChatMessage : INotifyPropertyChanged
{
    private string _role = string.Empty;
    private string _content = string.Empty;

    public string Role
    {
        get => _role;
        set { _role = value; OnPropertyChanged(); }
    }

    public string Content
    {
        get => _content;
        set { _content = value; OnPropertyChanged(); }
    }

    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsToolCall { get; set; }
    public string? ToolName { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
