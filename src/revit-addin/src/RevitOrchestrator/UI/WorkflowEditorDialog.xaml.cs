using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using RevitOrchestrator.Models;
using RevitOrchestrator.Pipe;

namespace RevitOrchestrator.UI;

/// <summary>
/// Dialog for creating and editing workflow definitions with governance metadata.
/// </summary>
public partial class WorkflowEditorDialog : Window
{
    private readonly WorkflowEditorViewModel _viewModel;

    /// <summary>
    /// Create a new empty workflow editor.
    /// </summary>
    public WorkflowEditorDialog()
    {
        _viewModel = new WorkflowEditorViewModel();
        DataContext = _viewModel;
        InitializeComponent();
        SetOwnerToRevitWindow();
    }

    /// <summary>
    /// Create an editor pre-populated with steps (from Recording/History).
    /// </summary>
    public WorkflowEditorDialog(WorkflowEditorViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        InitializeComponent();
        SetOwnerToRevitWindow();
    }

    private void SetOwnerToRevitWindow()
    {
        try
        {
            var revitHandle = Process.GetCurrentProcess().MainWindowHandle;
            if (revitHandle != IntPtr.Zero)
            {
                var helper = new WindowInteropHelper(this);
                helper.Owner = revitHandle;
            }
        }
        catch
        {
            // Ignore — dialog will still work, just not parented to Revit
        }
    }

    /// <summary>
    /// The resulting workflow definition after a successful save.
    /// </summary>
    public WorkflowDefinition? ResultDefinition => _viewModel.BuildDefinition();

    /// <summary>
    /// Set when the user clicked "Test Workflow" instead of Save/Cancel.
    /// The caller should close the dialog, run the test, and reopen.
    /// </summary>
    public bool TestRequested { get; private set; }

    /// <summary>
    /// Show the editor dialog with test support.  Handles the close → test →
    /// reopen loop so that Revit's ExternalEvent can process tool calls during
    /// the test (the modal dialog would otherwise block idle processing).
    /// </summary>
    public static async Task<WorkflowDefinition?> ShowWithTestSupportAsync(WorkflowEditorViewModel vm)
    {
        while (true)
        {
            var dialog = new WorkflowEditorDialog(vm);
            dialog.WireReviewHandler();
            var result = dialog.ShowDialog();

            if (result == true)
                return dialog.ResultDefinition;

            if (!dialog.TestRequested)
                return null; // User cancelled

            // Test requested — run outside the modal dialog so Revit
            // can process ExternalEvents for the actual tool calls.
            vm.IsTesting = true;
            vm.StatusText = "Running test workflow in Revit (dialog will reopen with results)...";
            await RunTestOutsideDialogAsync(vm);
            // Loop back to reopen the dialog with test results in the ViewModel
        }
    }

    private static async Task RunTestOutsideDialogAsync(WorkflowEditorViewModel vm)
    {
        if (App.Instance?.PipeListener is not { } listener || !listener.IsConnected)
        {
            vm.SetTestResults(false, 0, 0, 0, "Not connected to server");
            return;
        }

        var tcs = new TaskCompletionSource<bool>();

        void OnResponse(JsonElement payload)
        {
            var success = payload.TryGetProperty("success", out var s) && s.GetBoolean();
            var stepsCompleted = payload.TryGetProperty("steps_completed", out var sc) ? sc.GetInt32() : 0;
            var stepsTotal = payload.TryGetProperty("steps_total", out var st) ? st.GetInt32() : 0;
            var durationMs = payload.TryGetProperty("total_duration_ms", out var d) ? d.GetInt32() : 0;
            var error = payload.TryGetProperty("error", out var e) ? e.GetString() : null;
            vm.SetTestResults(success, stepsCompleted, stepsTotal, durationMs, error);
            tcs.TrySetResult(true);
        }

        listener.OnWorkflowTestResponse += OnResponse;
        try
        {
            var steps = vm.BuildSteps();
            await listener.TestWorkflowAsync(steps, vm.WorkflowDescription ?? "");

            // Wait for the response with a timeout
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            cts.Token.Register(() => tcs.TrySetCanceled());

            try
            {
                await tcs.Task;
            }
            catch (TaskCanceledException)
            {
                vm.SetTestResults(false, 0, 0, 0, "Test timed out after 60 seconds");
            }
        }
        catch (Exception ex)
        {
            vm.SetTestResults(false, 0, 0, 0, ex.Message);
        }
        finally
        {
            listener.OnWorkflowTestResponse -= OnResponse;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.WorkflowName))
        {
            MessageBox.Show("Please enter a workflow name.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!Regex.IsMatch(_viewModel.WorkflowName, @"^[\w\.\-]+$"))
        {
            MessageBox.Show("Workflow name can only contain letters, numbers, dots, hyphens, and underscores.",
                "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_viewModel.Steps.Count == 0)
        {
            MessageBox.Show("Workflow must have at least one step.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // If LLM review is requested but not yet performed, trigger it
        if (_viewModel.RequestLlmReview && !_viewModel.HasReviewResults)
        {
            _viewModel.StatusText = "Requesting LLM review...";
            _ = RunLlmReviewAsync();
            return;
        }

        DialogResult = true;
        Close();
    }

    private async Task RunLlmReviewAsync()
    {
        try
        {
            if (App.Instance?.PipeListener is not { } listener || !listener.IsConnected)
            {
                _viewModel.StatusText = "Not connected to server";
                return;
            }

            var steps = _viewModel.BuildSteps();
            await listener.ReviewWorkflowAsync(steps, _viewModel.WorkflowDescription ?? "");
            _viewModel.StatusText = "Review requested, waiting for response...";

            // The response will come back via the event handler wired below
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Review error: {ex.Message}";
        }
    }

    /// <summary>
    /// Wire up the review response handler from the pipe listener.
    /// Call this after construction if LLM review is needed.
    /// </summary>
    public void WireReviewHandler()
    {
        if (App.Instance?.PipeListener is { } listener)
        {
            listener.OnWorkflowReviewResponse += OnReviewResponse;
        }
    }

    private void OnReviewResponse(JsonElement payload)
    {
        Dispatcher.Invoke(() =>
        {
            var success = payload.TryGetProperty("success", out var s) && s.GetBoolean();
            if (success)
            {
                var summary = payload.TryGetProperty("summary", out var sumEl) ? sumEl.GetString() ?? "" : "";
                var flags = new List<string>();
                if (payload.TryGetProperty("flags", out var flagsEl))
                {
                    foreach (var flag in flagsEl.EnumerateArray())
                    {
                        flags.Add(flag.GetString() ?? "");
                    }
                }

                _viewModel.SetReviewResults(summary, flags);
                _viewModel.StatusText = "Review complete. Click Save again to confirm.";
            }
            else
            {
                var error = payload.TryGetProperty("error", out var e) ? e.GetString() ?? "Unknown error" : "Unknown error";
                _viewModel.StatusText = $"Review failed: {error}";
            }
        });
    }

    private void TestButton_Click(object sender, RoutedEventArgs e)
    {
        // Close the dialog so the caller can run the test while Revit is free
        // to process ExternalEvents.  The dialog will reopen with results.
        TestRequested = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Unwire event handlers
        if (App.Instance?.PipeListener is { } listener)
        {
            listener.OnWorkflowReviewResponse -= OnReviewResponse;
        }
        base.OnClosed(e);
    }
}
