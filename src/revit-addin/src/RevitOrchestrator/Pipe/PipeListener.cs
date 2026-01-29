using System.Text.Json;
using RevitOrchestrator.Execution;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.Pipe;

/// <summary>
/// Background listener that reads messages from the pipe and dispatches tool calls.
/// Implements reconnection with exponential backoff.
/// </summary>
public sealed class PipeListener : IDisposable
{
    private readonly string _pipeName;
    private readonly CommandQueue _commandQueue;
    private PipeClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public event Action<string>? OnStatusChanged;
    public event Action<PipeMessage>? OnMessageReceived;

    public PipeListener(string pipeName, CommandQueue commandQueue)
    {
        _pipeName = pipeName;
        _commandQueue = commandQueue;
    }

    public bool IsConnected => _client?.IsConnected ?? false;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _client?.Dispose();
        _client = null;
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        var backoffMs = 1000;
        const int maxBackoffMs = 30_000;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                OnStatusChanged?.Invoke("Connecting...");
                _client = new PipeClient(_pipeName);
                await _client.ConnectAsync(ct);
                OnStatusChanged?.Invoke("Connected");
                backoffMs = 1000; // Reset backoff on successful connection

                await ReadLoopAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Disconnected: {ex.Message}");
                _client?.Dispose();
                _client = null;
            }

            if (!ct.IsCancellationRequested)
            {
                OnStatusChanged?.Invoke($"Reconnecting in {backoffMs / 1000}s...");
                await Task.Delay(backoffMs, ct);
                backoffMs = Math.Min(backoffMs * 2, maxBackoffMs);
            }
        }

        OnStatusChanged?.Invoke("Stopped");
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && (_client?.IsConnected ?? false))
        {
            var message = await _client!.ReadAsync(ct);
            OnMessageReceived?.Invoke(message);

            switch (message.Type)
            {
                case "ping":
                    await _client.SendAsync(PipeMessage.Pong(), ct);
                    break;

                case "tool_call":
                    await HandleToolCallAsync(message, ct);
                    break;
            }
        }
    }

    private async Task HandleToolCallAsync(PipeMessage message, CancellationToken ct)
    {
        var toolCall = JsonSerializer.Deserialize<ToolCall>(message.Payload.GetRawText());
        if (toolCall is null) return;

        toolCall.CallId = message.Id;

        // Enqueue and wait for the result via ExternalEvent
        var result = await _commandQueue.EnqueueAsync(toolCall, ct);

        // Send result back
        var response = PipeMessage.Create("tool_result", result);
        await _client!.SendAsync(response, ct);
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
