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
    internal UIApplication? UiApplication { get; set; }
    internal PythonProcessManager? PythonProcess { get; private set; }

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

        // Auto-start the Python orchestrator process
        var (pythonPath, workDir) = PythonProcessManager.ResolvePaths();
        if (pythonPath != null && workDir != null)
        {
            PythonProcess = new PythonProcessManager(pythonPath, workDir);
            PythonProcess.OnLogMessage += msg => System.Diagnostics.Debug.WriteLine($"[Orchestrator] {msg}");
            PythonProcess.OnStatusChanged += status => System.Diagnostics.Debug.WriteLine($"[Orchestrator] Status: {status}");
            PythonProcess.Start();
        }
        else
        {
            System.Diagnostics.Debug.WriteLine(
                "[Orchestrator] Python server not found. " +
                "Check orchestrator-startup.log next to the DLL for details.");
        }

        // Start listening for pipe connections (has its own reconnect loop)
        PipeListener.Start();

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        ActionRecorder?.Dispose();
        PipeListener?.Stop();
        PipeListener?.Dispose();
        PythonProcess?.Dispose();
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
        // Wire approval dialog
        PipeListener.OnApprovalRequest = async (request) =>
        {
            var tcs = new TaskCompletionSource<bool>();
            RunOnRevitThread(_ =>
            {
                try
                {
                    var dialog = new UI.ApprovalDialog(request);
                    dialog.ShowDialog();
                    tcs.TrySetResult(dialog.IsApproved);
                }
                catch
                {
                    tcs.TrySetResult(false);
                }
            });
            return await tcs.Task;
        };

        // Wire mapping dialog for reconciliation
        PipeListener.OnMappingRequest = async (request) =>
        {
            var tcs = new TaskCompletionSource<MappingResponse>();
            RunOnRevitThread(_ =>
            {
                try
                {
                    var dialog = new UI.MappingDialog(request);
                    dialog.ShowDialog();
                    tcs.TrySetResult(dialog.Result);
                }
                catch
                {
                    tcs.TrySetResult(new MappingResponse { Action = "cancel" });
                }
            });
            return await tcs.Task;
        };

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
        CommandDispatcher.Register(new PlaceFamilyInstanceCommand());
        CommandDispatcher.Register(new ResolveSystemTypeCommand());
        CommandDispatcher.Register(new ResolveFamilyTypeCommand());
        CommandDispatcher.Register(new ResolveHostElementCommand());
        CommandDispatcher.Register(new RunDynamoGraphCommand());
        CommandDispatcher.Register(new RunPythonScriptCommand());
    }
}
