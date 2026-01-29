using Autodesk.Revit.UI;
using RevitOrchestrator.Commands;
using RevitOrchestrator.Execution;
using RevitOrchestrator.Pipe;
using RevitOrchestrator.UI;

namespace RevitOrchestrator;

/// <summary>
/// Revit add-in entry point. Sets up the pipe listener, command infrastructure,
/// and dockable chat panel.
/// </summary>
public sealed class App : IExternalApplication
{
    internal static App? Instance { get; private set; }

    internal CommandQueue CommandQueue { get; private set; } = null!;
    internal CommandDispatcher CommandDispatcher { get; private set; } = null!;
    internal RevitCommandHandler CommandHandler { get; private set; } = null!;
    internal PipeListener PipeListener { get; private set; } = null!;
    internal ExternalEvent? ExternalEvent { get; private set; }

    private static readonly DockablePaneId ChatPaneId =
        new(new Guid("B1E2F3A4-C5D6-7890-ABCD-EF1234567890"));

    public Result OnStartup(UIControlledApplication application)
    {
        Instance = this;

        // Set up command infrastructure
        CommandDispatcher = new CommandDispatcher();
        RegisterCommands();

        CommandQueue = new CommandQueue(); // ExternalEvent set after creation
        CommandHandler = new RevitCommandHandler(CommandQueue, CommandDispatcher);
        ExternalEvent = ExternalEvent.Create(CommandHandler);

        // Update the queue with the external event (circular dependency resolution)
        CommandQueue = new CommandQueue(ExternalEvent);

        // Set up pipe listener
        PipeListener = new PipeListener("RevitOrchestrator", CommandQueue);

        // Register dockable pane
        application.RegisterDockablePane(
            ChatPaneId,
            "Revit Orchestrator",
            new ChatPanel());

        // Create ribbon panel and button
        var panel = application.CreateRibbonPanel("Orchestrator");
        var buttonData = new PushButtonData(
            "OrchestratorCmd",
            "Chat\nPanel",
            typeof(App).Assembly.Location,
            typeof(OrchestratorCommand).FullName);
        buttonData.ToolTip = "Show or hide the Revit Orchestrator chat panel";
        panel.AddItem(buttonData);

        // Start listening for pipe connections
        PipeListener.Start();

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        PipeListener?.Stop();
        PipeListener?.Dispose();
        Instance = null;
        return Result.Succeeded;
    }

    private void RegisterCommands()
    {
        CommandDispatcher.Register(new GetElementInfoCommand());
        CommandDispatcher.Register(new CreateWallCommand());
    }
}
