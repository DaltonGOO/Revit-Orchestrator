using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Autodesk.Revit.DB;
using RevitOrchestrator.Execution;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.Commands;

/// <summary>
/// Executes a Dynamo graph inside Revit using DynamoRevitApp.ExecuteDynamoCommand.
/// </summary>
public sealed class RunDynamoGraphCommand : IRevitCommand
{
    public string ToolName => "dynamo.run_graph";
    public bool RequiresTransaction => false; // Dynamo manages its own transactions

    /// <summary>Cached DynamoRevitApp instance — reused across executions within the same Revit session.</summary>
    private static object? _dynRevitApp;

    /// <summary>Cached MethodInfo for ExecuteDynamoCommand — avoids repeated reflection lookups.</summary>
    private static MethodInfo? _execMethod;

    public ToolResult Execute(Document doc, JsonElement args)
    {
        var graphPath = args.GetProperty("graph_path").GetString()?.Trim();
        if (string.IsNullOrEmpty(graphPath))
            return ToolResult.Fail("", "INVALID_ARGUMENT", "graph_path is required");

        if (!File.Exists(graphPath))
        {
            var dir = Path.GetDirectoryName(graphPath);
            var dirExists = !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
            var detail = dirExists
                ? $"File not found: {graphPath} (directory exists, check filename)"
                : $"Path not found: {graphPath} (directory does not exist)";
            return ToolResult.Fail("", "FILE_NOT_FOUND", detail);
        }

        var uiApp = App.Instance?.UiApplication;
        if (uiApp == null)
            return ToolResult.Fail("", "DYNAMO_EXECUTION_ERROR",
                "UIApplication is null — not in Revit API context");

        try
        {
            // ── Get or create DynamoRevitApp ──
            EnsureDynamoRevitApp();
            if (_dynRevitApp == null || _execMethod == null)
                return ToolResult.Fail("", "DYNAMO_NOT_LOADED",
                    "Could not create DynamoRevitApp. Is Dynamo for Revit installed and loaded?");

            // ── Execute — blocks until Dynamo finishes ──
            var journalData = new Dictionary<string, string>
            {
                { "dynPath", graphPath },
                { "dynShowUI", "False" },
                { "dynAutomation", "True" },
                { "dynPathExecute", "True" },
                { "dynModelShutDown", "False" },
            };

            InvokeDynamoCommand(journalData, uiApp);

            return ToolResult.Ok("", new Dictionary<string, object?>
            {
                ["message"] = $"Dynamo graph executed: {Path.GetFileName(graphPath)}",
                ["graph_path"] = graphPath,
            });
        }
        catch (TargetInvocationException tie)
        {
            var inner = tie.InnerException ?? tie;
            return ToolResult.Fail("", "DYNAMO_EXECUTION_ERROR",
                $"Dynamo graph execution failed: {inner.Message}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail("", "DYNAMO_EXECUTION_ERROR",
                $"Failed to execute Dynamo graph: {ex.Message}");
        }
    }

    // ── Helpers ──

    /// <summary>
    /// Open a Dynamo graph in the Dynamo UI for editing/debugging.
    /// Reuses the cached DynamoRevitApp instance from previous executions.
    /// </summary>
    public static void OpenInDynamoUI(string graphPath, object uiApp)
    {
        EnsureDynamoRevitApp();
        if (_dynRevitApp == null || _execMethod == null)
            throw new InvalidOperationException(
                "DynamoRevitApp not available. Is Dynamo for Revit installed?");

        var journalData = new Dictionary<string, string>
        {
            { "dynPath", graphPath },
            { "dynShowUI", "True" },
            { "dynAutomation", "False" },
            { "dynPathExecute", "False" },
            { "dynModelShutDown", "False" },
        };

        InvokeDynamoCommand(journalData, uiApp);
    }

    /// <summary>
    /// Invoke ExecuteDynamoCommand with the given journal data and UIApplication.
    /// Adapts to whatever parameter signature the method has.
    /// </summary>
    private static void InvokeDynamoCommand(Dictionary<string, string> journalData, object uiApp)
    {
        var methodParams = _execMethod!.GetParameters();
        object?[] invokeArgs;

        if (methodParams.Length == 2)
        {
            invokeArgs = new object[] { journalData, uiApp };
        }
        else if (methodParams.Length == 1)
        {
            if (methodParams[0].ParameterType == typeof(Dictionary<string, string>))
                invokeArgs = new object[] { journalData };
            else
                invokeArgs = new object[] { uiApp };
        }
        else
        {
            invokeArgs = new object[methodParams.Length];
            for (int i = 0; i < methodParams.Length; i++)
            {
                if (methodParams[i].ParameterType == typeof(Dictionary<string, string>))
                    invokeArgs[i] = journalData;
                else if (methodParams[i].ParameterType.Name.Contains("UIApplication"))
                    invokeArgs[i] = uiApp;
                else if (methodParams[i].HasDefaultValue)
                    invokeArgs[i] = methodParams[i].DefaultValue;
                else
                    invokeArgs[i] = null;
            }
        }

        _execMethod.Invoke(_dynRevitApp, invokeArgs);
    }

    /// <summary>
    /// Create or reuse the DynamoRevitApp instance and resolve ExecuteDynamoCommand.
    /// DynamoRevitDS is part of the Dynamo for Revit installation.
    /// </summary>
    private static void EnsureDynamoRevitApp()
    {
        if (_dynRevitApp != null && _execMethod != null) return;

        // Reset in case of partial init from a previous failed attempt
        _dynRevitApp = null;
        _execMethod = null;

        // DynamoRevitDS should already be loadable when Dynamo for Revit is installed
        var handle = Activator.CreateInstance("DynamoRevitDS", "Dynamo.Applications.DynamoRevitApp");
        if (handle == null)
            throw new FileNotFoundException("Could not create DynamoRevitApp. Is Dynamo for Revit installed?");

        var app = handle.Unwrap();
        if (app == null)
            throw new FileNotFoundException("Could not unwrap DynamoRevitApp instance.");

        // Find ExecuteDynamoCommand — try multiple matching strategies
        var allMethods = app.GetType().GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // Strategy 1: exact name + 2 params + first param is Dictionary<string,string>
        var method = allMethods
            .FirstOrDefault(m => m.Name == "ExecuteDynamoCommand"
                && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType == typeof(Dictionary<string, string>));

        // Strategy 2: exact name, any param count
        method ??= allMethods
            .FirstOrDefault(m => m.Name == "ExecuteDynamoCommand");

        // Strategy 3: name contains "Execute" and "Dynamo"
        method ??= allMethods
            .FirstOrDefault(m => m.Name.Contains("Execute") && m.Name.Contains("Dynamo"));

        if (method == null)
        {
            // Dump all methods for diagnostics
            var methodList = allMethods
                .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
                .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})")
                .Distinct();
            throw new MissingMethodException(
                $"No Execute method found on {app.GetType().FullName}. "
                + $"Available methods: [{string.Join("; ", methodList)}]");
        }

        _dynRevitApp = app;
        _execMethod = method;
    }

}
