using System.Text.Json;
using System.Windows;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.UI;

/// <summary>
/// Modal dialog for approving or denying tool executions.
/// </summary>
public partial class ApprovalDialog : Window
{
    public bool IsApproved { get; private set; }

    public ApprovalDialog(ApprovalRequest request)
    {
        InitializeComponent();

        ToolNameText.Text = request.ToolName;
        SideEffectsList.ItemsSource = request.SideEffects;

        try
        {
            ArgsText.Text = JsonSerializer.Serialize(
                request.Args,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            ArgsText.Text = "(unable to display arguments)";
        }
    }

    private void OnApprove(object sender, RoutedEventArgs e)
    {
        IsApproved = true;
        DialogResult = true;
        Close();
    }

    private void OnDeny(object sender, RoutedEventArgs e)
    {
        IsApproved = false;
        DialogResult = false;
        Close();
    }
}
