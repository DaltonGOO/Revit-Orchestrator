using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;

namespace RevitOrchestrator.UI;

/// <summary>
/// Dialog for editing a tool's contract (parameters, execution modes, side effects, permissions).
/// </summary>
public partial class ToolEditorDialog : Window
{
    private readonly ToolEditorViewModel _viewModel;

    public ToolEditorDialog(ToolEditorViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        InitializeComponent();
        Title = $"Edit Tool Contract: {_viewModel.ToolName}";
        SetOwnerToRevitWindow();
    }

    /// <summary>
    /// The edited definition dictionary, or null if cancelled.
    /// </summary>
    public Dictionary<string, object?>? ResultDefinition { get; private set; }

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
        catch { }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ResultDefinition = _viewModel.ToDefinition();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
