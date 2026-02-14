using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitOrchestrator.Capture;
using RevitOrchestrator.Commands;
using RevitOrchestrator.Execution;
using RevitOrchestrator.Models;
using RevitOrchestrator.Pipe;
using RevitOrchestrator.Recording;
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
    internal ActionRecorder? ActionRecorder { get; private set; }
    internal UIApplication? UiApplication { get; private set; }

    private static readonly DockablePaneId ChatPaneId =
        new(new Guid("B1E2F3A4-C5D6-7890-ABCD-EF1234567890"));

    private ChatPanel? _chatPanel;

    public Result OnStartup(UIControlledApplication application)
    {
        Instance = this;

        // Set up command infrastructure
        CommandDispatcher = new CommandDispatcher();
        RegisterCommands();

        // Create handler first, then wire up ExternalEvent and CommandQueue.
        // CommandQueue needs ExternalEvent, and ExternalEvent needs CommandHandler,
        // so we create the handler with a temporary queue, then replace it.
        var tempQueue = new CommandQueue();
        CommandHandler = new RevitCommandHandler(tempQueue, CommandDispatcher);
        ExternalEvent = ExternalEvent.Create(CommandHandler);
        CommandQueue = new CommandQueue(ExternalEvent);
        // Point the handler at the real queue so it dequeues from the right place.
        CommandHandler.SetCommandQueue(CommandQueue);

        // Set up pipe listener
        PipeListener = new PipeListener("RevitOrchestrator", CommandQueue);

        // Register dockable pane
        _chatPanel = new ChatPanel();
        application.RegisterDockablePane(
            ChatPaneId,
            "Revit Orchestrator",
            _chatPanel);

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
        ActionRecorder?.Dispose();
        PipeListener?.Stop();
        PipeListener?.Dispose();
        Instance = null;
        return Result.Succeeded;
    }

    /// <summary>
    /// Initialize the action recorder with UIApplication context.
    /// Called when we first have access to UIApplication.
    /// </summary>
    internal void InitializeActionRecorder(UIApplication uiApp)
    {
        if (ActionRecorder != null) return;

        UiApplication = uiApp;
        ActionRecorder = new ActionRecorder(uiApp);
        _chatPanel?.InitializeRecording();

        // Wire pipe listener callbacks that need Revit API access
        WirePipeListenerCallbacks();
    }

    private void WirePipeListenerCallbacks()
    {
        PipeListener.GetRunContext = () =>
        {
            try
            {
                var doc = UiApplication?.ActiveUIDocument?.Document;
                return new RunContext
                {
                    RevitVersion = UiApplication?.Application.VersionNumber ?? "",
                    DocumentTitle = doc?.Title ?? "",
                    DocumentGuid = doc?.ProjectInformation?.UniqueId ?? "",
                    IsWorksharingEnabled = doc?.IsWorkshared ?? false,
                    ActiveViewType = UiApplication?.ActiveUIDocument?.ActiveView?.ViewType.ToString() ?? "",
                    UserName = UiApplication?.Application.Username ?? "",
                };
            }
            catch
            {
                return new RunContext();
            }
        };

        PipeListener.CaptureScreenshot = () =>
        {
            // ExportImage requires the Revit API context. Marshal via
            // ExternalEvent and block until it completes.
            var tcs = new TaskCompletionSource<byte[]?>();
            RunOnRevitThread(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    tcs.TrySetResult(doc != null ? ViewportCapture.CaptureActiveView(doc) : null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            try { return tcs.Task.GetAwaiter().GetResult(); }
            catch { return null; }
        };

        PipeListener.GetElementIdentities = (elementIds) =>
        {
            var tcs = new TaskCompletionSource<List<Dictionary<string, object?>>>();
            RunOnRevitThread(uiApp =>
            {
                var list = new List<Dictionary<string, object?>>();
                try
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc != null)
                    {
                        foreach (var eid in elementIds)
                        {
#if REVIT2025 || REVIT2026
                            var element = doc.GetElement(new ElementId(eid));
#else
                            var element = doc.GetElement(new ElementId((int)eid));
#endif
                            if (element == null) continue;
                            var identity = ElementIdentityBuilder.Build(doc, element);
                            list.Add(identity.ToDictionary());
                        }
                    }
                }
                catch { }
                tcs.TrySetResult(list);
            });
            try { return tcs.Task.GetAwaiter().GetResult(); }
            catch { return new List<Dictionary<string, object?>>(); }
        };
    }

    /// <summary>
    /// Run an action on the Revit API thread via ExternalEvent.
    /// Use this for any Revit API calls from UI button clicks in dockable panes,
    /// since those run on the main thread but outside the API context.
    /// </summary>
    internal void RunOnRevitThread(Action<UIApplication> action)
    {
        CommandHandler.EnqueueAction(action);
        ExternalEvent?.Raise();
    }

    private void RegisterCommands()
    {
        CommandDispatcher.Register(new GetElementInfoCommand());
        CommandDispatcher.Register(new CreateWallCommand());
        CommandDispatcher.Register(new CreateElementCommand());
        CommandDispatcher.Register(new RunDynamoGraphCommand());
        CommandDispatcher.Register(new RunPythonScriptCommand());
    }
}
