using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.UI;

namespace RevitOrchestrator.UI;

/// <summary>
/// WPF dockable chat panel for Revit Orchestrator.
/// </summary>
public partial class ChatPanel : UserControl, IDockablePaneProvider
{
    private readonly ChatPanelViewModel _viewModel;
    private readonly ToolManagerViewModel _toolManagerViewModel;
    private readonly HistoryViewModel _historyViewModel;
    private readonly RecordingViewModel _recordingViewModel;

    public ChatPanel()
    {
        _viewModel = new ChatPanelViewModel();
        _toolManagerViewModel = new ToolManagerViewModel();
        _historyViewModel = new HistoryViewModel();
        _recordingViewModel = new RecordingViewModel();

        DataContext = _viewModel;
        InitializeComponent();

        // Set the Tools tab DataContext to the tool manager VM
        ToolsGrid.DataContext = _toolManagerViewModel;

        // Set the History tab DataContext to the history VM
        HistoryGrid.DataContext = _historyViewModel;

        // Set the Recording tab DataContext to the recording VM
        RecordingGrid.DataContext = _recordingViewModel;

        // Initialize recording VM with the action recorder when available
        if (App.Instance?.ActionRecorder != null)
        {
            _recordingViewModel.Initialize(App.Instance.ActionRecorder);
        }

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

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is TabControl && MainTabControl.SelectedItem == ToolsTab)
        {
            // Auto-refresh tool list when switching to the Tools tab
            _toolManagerViewModel.RequestRefresh();
        }
    }

    /// <summary>
    /// Late initialization for the recording view model (called after App.ActionRecorder is ready).
    /// </summary>
    internal void InitializeRecording()
    {
        if (App.Instance?.ActionRecorder != null)
        {
            _recordingViewModel.Initialize(App.Instance.ActionRecorder);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog();
        dialog.ShowDialog();
    }
}
