using Autodesk.Revit.DB;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.Execution;

/// <summary>
/// Helper that wraps Revit API calls in a transaction.
/// </summary>
public static class TransactionWrapper
{
    /// <summary>
    /// Execute an action within a Revit transaction.
    /// </summary>
    public static ToolResult Execute(
        Document doc,
        string transactionName,
        Func<Document, ToolResult> action)
    {
        using var transaction = new Transaction(doc, transactionName);

        try
        {
            transaction.Start();
            var result = action(doc);

            if (result.Success)
            {
                var status = transaction.Commit();
                if (status != TransactionStatus.Committed)
                {
                    return ToolResult.Fail(
                        result.CallId,
                        "REVIT_TRANSACTION_FAILED",
                        $"Transaction commit returned status: {status}");
                }
            }
            else
            {
                transaction.RollBack();
            }

            return result;
        }
        catch (Exception ex)
        {
            if (transaction.HasStarted())
                transaction.RollBack();

            return ToolResult.Fail("", "REVIT_API_ERROR", ex.Message);
        }
    }
}
