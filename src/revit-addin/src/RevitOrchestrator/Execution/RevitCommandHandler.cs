using System.Collections.Concurrent;
using Autodesk.Revit.UI;

namespace RevitOrchestrator.Execution;

/// <summary>
/// ExternalEventHandler that processes tool calls on the Revit main thread.
/// </summary>
public sealed class RevitCommandHandler : IExternalEventHandler
{
    private CommandQueue _commandQueue;
    private readonly CommandDispatcher _dispatcher;
    private UIApplication? _uiApp;
    private readonly ConcurrentQueue<Action<UIApplication>> _actionQueue = new();

    public RevitCommandHandler(CommandQueue commandQueue, CommandDispatcher dispatcher)
    {
        _commandQueue = commandQueue;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Replace the command queue after construction (needed to resolve
    /// circular dependency between CommandQueue and ExternalEvent).
    /// </summary>
    internal void SetCommandQueue(CommandQueue commandQueue)
    {
        _commandQueue = commandQueue;
    }

    /// <summary>
    /// Enqueue an arbitrary action to run on the Revit API thread.
    /// Call ExternalEvent.Raise() after this to trigger execution.
    /// </summary>
    internal void EnqueueAction(Action<UIApplication> action)
    {
        _actionQueue.Enqueue(action);
    }

    public string GetName() => "RevitOrchestrator.CommandHandler";

    /// <summary>
    /// Called by Revit on the main thread when ExternalEvent.Raise() is triggered.
    /// Processes all queued commands and actions.
    /// </summary>
    public void Execute(UIApplication app)
    {
        _uiApp = app;

        // Initialize action recorder if not already done
        App.Instance?.InitializeActionRecorder(app);

        // Process general-purpose actions first (these don't need a document)
        while (_actionQueue.TryDequeue(out var action))
        {
            try
            {
                action(app);
            }
            catch (Exception)
            {
                // Swallow to prevent crashing Revit
            }
        }

        var doc = app.ActiveUIDocument?.Document;

        if (doc is null)
            return;

        // Process all queued tool-call commands
        while (_commandQueue.TryDequeue(out var context))
        {
            if (context is null) continue;

            try
            {
                var result = _dispatcher.Dispatch(doc, context.ToolCall);
                context.SetResult(result);
            }
            catch (Exception ex)
            {
                context.SetException(ex);
            }
        }
    }
}
