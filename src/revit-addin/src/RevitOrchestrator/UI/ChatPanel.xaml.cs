using System.Windows.Controls;
using Autodesk.Revit.UI;

namespace RevitOrchestrator.UI;

/// <summary>
/// WPF dockable chat panel for Revit Orchestrator.
/// </summary>
public partial class ChatPanel : UserControl, IDockablePaneProvider
{
    private readonly ChatPanelViewModel _viewModel;

    public ChatPanel()
    {
        _viewModel = new ChatPanelViewModel();
        DataContext = _viewModel;
        InitializeComponent();

        // Auto-scroll to bottom when new messages arrive
        _viewModel.Messages.CollectionChanged += (_, _) =>
        {
            MessagesScroller.ScrollToBottom();
        };
    }

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = this;
        data.InitialState = new DockablePaneState
        {
            DockPosition = DockPosition.Right,
        };
    }
}
