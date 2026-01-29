using Autodesk.Revit.UI;

namespace RevitOrchestrator.Execution;

/// <summary>
/// ExternalEventHandler that processes tool calls on the Revit main thread.
/// </summary>
public sealed class RevitCommandHandler : IExternalEventHandler
{
    private readonly CommandQueue _commandQueue;
    private readonly CommandDispatcher _dispatcher;
    private UIApplication? _uiApp;

    public RevitCommandHandler(CommandQueue commandQueue, CommandDispatcher dispatcher)
    {
        _commandQueue = commandQueue;
        _dispatcher = dispatcher;
    }

    public string GetName() => "RevitOrchestrator.CommandHandler";

    /// <summary>
    /// Called by Revit on the main thread when ExternalEvent.Raise() is triggered.
    /// Processes all queued commands.
    /// </summary>
    public void Execute(UIApplication app)
    {
        _uiApp = app;
        var doc = app.ActiveUIDocument?.Document;

        if (doc is null)
            return;

        // Process all queued commands
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
