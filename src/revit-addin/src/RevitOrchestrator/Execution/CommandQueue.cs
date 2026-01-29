using System.Collections.Concurrent;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.Execution;

/// <summary>
/// Thread-safe command queue that bridges the pipe listener thread
/// with the Revit main thread via ExternalEvent.
/// </summary>
public sealed class CommandQueue
{
    private readonly ConcurrentQueue<ToolCallContext> _queue = new();
    private readonly Autodesk.Revit.UI.ExternalEvent? _externalEvent;

    public CommandQueue(Autodesk.Revit.UI.ExternalEvent? externalEvent = null)
    {
        _externalEvent = externalEvent;
    }

    /// <summary>
    /// Enqueue a tool call and return a task that completes when the command
    /// has been executed on the Revit main thread.
    /// </summary>
    public Task<ToolResult> EnqueueAsync(ToolCall toolCall, CancellationToken ct = default)
    {
        var context = new ToolCallContext(toolCall);
        _queue.Enqueue(context);

        // Signal Revit to call our ExternalEvent handler
        _externalEvent?.Raise();

        // Register cancellation
        ct.Register(() => context.TrySetCanceled());

        return context.Task;
    }

    /// <summary>
    /// Try to dequeue the next pending command. Called from the Revit main thread.
    /// </summary>
    public bool TryDequeue(out ToolCallContext? context)
    {
        return _queue.TryDequeue(out context);
    }

    public int Count => _queue.Count;
}

/// <summary>
/// Wraps a tool call with a TaskCompletionSource for async result delivery.
/// </summary>
public sealed class ToolCallContext
{
    private readonly TaskCompletionSource<ToolResult> _tcs = new();

    public ToolCallContext(ToolCall toolCall)
    {
        ToolCall = toolCall;
    }

    public ToolCall ToolCall { get; }
    public Task<ToolResult> Task => _tcs.Task;

    public void SetResult(ToolResult result) => _tcs.TrySetResult(result);
    public void SetException(Exception ex) => _tcs.TrySetException(ex);
    public void TrySetCanceled() => _tcs.TrySetCanceled();
}
