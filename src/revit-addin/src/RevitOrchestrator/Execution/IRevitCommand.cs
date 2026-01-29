using System.Text.Json;
using RevitOrchestrator.Models;
using Autodesk.Revit.DB;

namespace RevitOrchestrator.Execution;

/// <summary>
/// Interface for Revit commands that execute tool calls on the main thread.
/// </summary>
public interface IRevitCommand
{
    /// <summary>
    /// The tool name this command handles (e.g., "revit.create_wall").
    /// </summary>
    string ToolName { get; }

    /// <summary>
    /// Whether this command requires a Revit transaction.
    /// </summary>
    bool RequiresTransaction { get; }

    /// <summary>
    /// Execute the command with the given arguments.
    /// </summary>
    ToolResult Execute(Document doc, JsonElement args);
}
