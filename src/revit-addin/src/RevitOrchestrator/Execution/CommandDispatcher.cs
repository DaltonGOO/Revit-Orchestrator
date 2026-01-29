using System.Diagnostics;
using System.Text.Json;
using Autodesk.Revit.DB;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.Execution;

/// <summary>
/// Routes tool names to their IRevitCommand handlers and executes them.
/// </summary>
public sealed class CommandDispatcher
{
    private readonly Dictionary<string, IRevitCommand> _commands = new();

    /// <summary>
    /// Register a command handler.
    /// </summary>
    public void Register(IRevitCommand command)
    {
        _commands[command.ToolName] = command;
    }

    /// <summary>
    /// Dispatch a tool call to the appropriate command handler.
    /// </summary>
    public ToolResult Dispatch(Document doc, ToolCall toolCall)
    {
        var sw = Stopwatch.StartNew();

        if (!_commands.TryGetValue(toolCall.ToolName, out var command))
        {
            return ToolResult.Fail(
                toolCall.CallId,
                "TOOL_NOT_FOUND",
                $"No command registered for tool '{toolCall.ToolName}'");
        }

        try
        {
            ToolResult result;

            if (command.RequiresTransaction)
            {
                result = TransactionWrapper.Execute(
                    doc,
                    $"Orchestrator: {toolCall.ToolName}",
                    d => command.Execute(d, toolCall.Args));
            }
            else
            {
                result = command.Execute(doc, toolCall.Args);
            }

            sw.Stop();
            result.CallId = toolCall.CallId;
            result.DurationMs = sw.ElapsedMilliseconds;
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ToolResult.Fail(
                toolCall.CallId,
                "REVIT_API_ERROR",
                ex.Message,
                sw.ElapsedMilliseconds);
        }
    }
}
