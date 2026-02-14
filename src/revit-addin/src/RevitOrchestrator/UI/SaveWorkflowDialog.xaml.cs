using System.Text.RegularExpressions;
using System.Windows;
using RevitOrchestrator.Models;
using RevitOrchestrator.Pipe;

namespace RevitOrchestrator.UI;

/// <summary>
/// Dialog for saving selected execution events as a reusable workflow.
/// </summary>
public partial class SaveWorkflowDialog : Window
{
    private readonly List<ExecutionEvent>? _executionSteps;
    private readonly List<WorkflowStep>? _workflowSteps;

    /// <summary>
    /// Create dialog from execution events (History tab).
    /// </summary>
    public SaveWorkflowDialog(IEnumerable<ExecutionEvent> selectedEvents)
    {
        InitializeComponent();

        _executionSteps = selectedEvents.OrderBy(e => e.Sequence).ToList();
        StepsList.ItemsSource = _executionSteps.Select(e => new { ToolName = e.ToolName, Summary = e.ArgsJson });

        // Generate default name from first tool
        if (_executionSteps.Count > 0)
        {
            var firstTool = _executionSteps[0].ToolName;
            var safeName = Regex.Replace(firstTool, @"[^\w\.]", "_");
            NameTextBox.Text = $"captured.{safeName}";
        }
    }

    /// <summary>
    /// Create dialog from workflow steps (Recording tab).
    /// </summary>
    public SaveWorkflowDialog(IEnumerable<WorkflowStep> steps, string defaultDescription)
    {
        InitializeComponent();

        _workflowSteps = steps.ToList();
        StepsList.ItemsSource = _workflowSteps.Select(s => new {
            ToolName = s.ToolName,
            Summary = s.Args?.Count > 0 ? $"{s.Args.Count} parameters" : "no parameters"
        });

        DescriptionTextBox.Text = defaultDescription;

        // Generate default name from first tool
        if (_workflowSteps.Count > 0)
        {
            var firstTool = _workflowSteps[0].ToolName ?? "workflow";
            var safeName = Regex.Replace(firstTool, @"[^\w\.]", "_");
            NameTextBox.Text = $"recorded.{safeName}";
        }
    }

    /// <summary>
    /// The workflow name entered by the user.
    /// </summary>
    public string WorkflowName => NameTextBox.Text.Trim();

    /// <summary>
    /// The workflow description entered by the user.
    /// </summary>
    public string WorkflowDescription => DescriptionTextBox.Text.Trim();

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(WorkflowName))
        {
            MessageBox.Show("Please enter a workflow name.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            NameTextBox.Focus();
            return;
        }

        // Validate name is a valid identifier
        if (!Regex.IsMatch(WorkflowName, @"^[\w\.\-]+$"))
        {
            MessageBox.Show("Workflow name can only contain letters, numbers, dots, hyphens, and underscores.",
                "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            NameTextBox.Focus();
            return;
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
