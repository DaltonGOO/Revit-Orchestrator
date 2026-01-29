using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.UI;

/// <summary>
/// MVVM ViewModel for the chat panel.
/// </summary>
public sealed class ChatPanelViewModel : INotifyPropertyChanged
{
    private string _inputText = string.Empty;
    private string _connectionStatus = "Disconnected";
    private Brush _statusColor = Brushes.Red;

    public ChatPanelViewModel()
    {
        SendCommand = new RelayCommand(OnSend, () => !string.IsNullOrWhiteSpace(InputText));

        // Subscribe to pipe listener status changes
        if (App.Instance?.PipeListener is { } listener)
        {
            listener.OnStatusChanged += status =>
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    ConnectionStatus = status;
                    StatusColor = listener.IsConnected ? Brushes.LimeGreen : Brushes.Red;
                });
            };
        }
    }

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public string InputText
    {
        get => _inputText;
        set
        {
            _inputText = value;
            OnPropertyChanged();
            (SendCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        set { _connectionStatus = value; OnPropertyChanged(); }
    }

    public Brush StatusColor
    {
        get => _statusColor;
        set { _statusColor = value; OnPropertyChanged(); }
    }

    public ICommand SendCommand { get; }

    private void OnSend()
    {
        if (string.IsNullOrWhiteSpace(InputText)) return;

        var message = new ChatMessage
        {
            Role = "user",
            Content = InputText.Trim(),
        };
        Messages.Add(message);
        InputText = string.Empty;

        // TODO: Send to MCP server via pipe for LLM processing
        Messages.Add(new ChatMessage
        {
            Role = "system",
            Content = "Message received. LLM routing not yet connected.",
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Simple ICommand implementation.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
