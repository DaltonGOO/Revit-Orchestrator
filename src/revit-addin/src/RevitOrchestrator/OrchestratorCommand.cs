using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitOrchestrator;

/// <summary>
/// External command that toggles the dockable chat panel visibility.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class OrchestratorCommand : IExternalCommand
{
    private static readonly DockablePaneId ChatPaneId =
        new(new Guid("B1E2F3A4-C5D6-7890-ABCD-EF1234567890"));

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var pane = commandData.Application.GetDockablePane(ChatPaneId);

        if (pane.IsShown())
            pane.Hide();
        else
            pane.Show();

        return Result.Succeeded;
    }
}
