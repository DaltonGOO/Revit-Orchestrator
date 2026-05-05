using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    private bool _isWaiting;
    private ChatMessage? _thinkingMessage;

    // Capture the dispatcher at construction time — Application.Current
    // can be null in Revit dockable panes.
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public ChatPanelViewModel()
    {
        SendCommand = new RelayCommand(OnSend, () => !string.IsNullOrWhiteSpace(InputText) && !IsWaiting);
        CancelCommand = new RelayCommand(OnCancel, () => IsWaiting);

        // Subscribe to pipe listener events
        if (App.Instance?.PipeListener is { } listener)
        {
            listener.OnStatusChanged += status =>
            {
                _dispatcher.Invoke(() =>
                {
                    ConnectionStatus = status;
                    StatusColor = listener.IsConnected ? Brushes.LimeGreen : Brushes.Red;
                });
            };

            listener.OnChatStatus += status =>
            {
                _dispatcher.Invoke(() =>
                {
                    if (_thinkingMessage is not null)
                    {
                        _thinkingMessage.Content = status;
                    }
                    else
                    {
                        _thinkingMessage = new ChatMessage
                        {
                            Role = "system",
                            Content = status,
                        };
                        Messages.Add(_thinkingMessage);
                    }
                });
            };

            listener.OnChatResponse += content =>
            {
                _dispatcher.Invoke(() =>
                {
                    // Remove the thinking message
                    if (_thinkingMessage is not null)
                    {
                        Messages.Remove(_thinkingMessage);
                        _thinkingMessage = null;
                    }

                    Messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content = content,
                    });

                    IsWaiting = false;
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

    public bool IsWaiting
    {
        get => _isWaiting;
        set
        {
            _isWaiting = value;
            OnPropertyChanged();
            (SendCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public ICommand SendCommand { get; }
    public ICommand CancelCommand { get; }

    private async void OnSend()
    {
        if (string.IsNullOrWhiteSpace(InputText) || IsWaiting) return;

        var userText = InputText.Trim();
        Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = userText,
        });
        InputText = string.Empty;

        if (App.Instance?.PipeListener is not { } listener || !listener.IsConnected)
        {
            Messages.Add(new ChatMessage
            {
                Role = "system",
                Content = "Not connected to the orchestrator server.",
            });
            return;
        }

        IsWaiting = true;

        try
        {
            await listener.SendChatMessageAsync(userText);
        }
        catch (Exception ex)
        {
            IsWaiting = false;
            Messages.Add(new ChatMessage
            {
                Role = "system",
                Content = $"Failed to send message: {ex.Message}",
            });
        }
    }

    private async void OnCancel()
    {
        if (!IsWaiting) return;
        if (App.Instance?.PipeListener is not { } listener || !listener.IsConnected)
        {
            // No connection to cancel through — just unfreeze locally.
            IsWaiting = false;
            return;
        }

        try
        {
            await listener.SendChatCancelAsync();
            // Don't clear IsWaiting here — Python will send a "Cancelled by
            // user" chat_response, and the existing OnChatResponse handler
            // flips IsWaiting off. That keeps the UX consistent if the cancel
            // races with an in-flight response.
        }
        catch (Exception ex)
        {
            IsWaiting = false;
            Messages.Add(new ChatMessage
            {
                Role = "system",
                Content = $"Failed to cancel: {ex.Message}",
            });
        }
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

/// <summary>
/// Generic ICommand implementation with parameter support.
/// </summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke((T?)parameter) ?? true;
    public void Execute(object? parameter) => _execute((T?)parameter);

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
