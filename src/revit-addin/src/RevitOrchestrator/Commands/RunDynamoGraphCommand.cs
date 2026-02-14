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
/// Executes a Dynamo graph via the Revit Dynamo integration.
/// Uses reflection to interact with Dynamo to avoid hard dependencies.
/// </summary>
public sealed class RunDynamoGraphCommand : IRevitCommand
{
    public string ToolName => "dynamo.run_graph";
    public bool RequiresTransaction => false; // Dynamo manages its own transactions

    // Cache for Dynamo types to avoid repeated reflection
    private static Type? _dynamoRevitType;
    private static bool _dynamoChecked;

    public ToolResult Execute(Document doc, JsonElement args)
    {
        var graphPath = args.GetProperty("graph_path").GetString();
        if (string.IsNullOrEmpty(graphPath))
        {
            return ToolResult.Fail("", "INVALID_ARGUMENT", "graph_path is required");
        }

        if (!File.Exists(graphPath))
        {
            return ToolResult.Fail("", "FILE_NOT_FOUND", $"Dynamo graph not found: {graphPath}");
        }

        // Parse inputs if provided
        var inputs = new Dictionary<string, object?>();
        if (args.TryGetProperty("inputs", out var inputsElement))
        {
            foreach (var prop in inputsElement.EnumerateObject())
            {
                inputs[prop.Name] = GetJsonValue(prop.Value);
            }
        }

        // Determine execution mode
        var executionMode = "headless";
        if (args.TryGetProperty("execution_mode", out var modeEl))
            executionMode = modeEl.GetString() ?? "headless";

        try
        {
            // Interactive mode: open the graph in Dynamo UI for the user to interact with
            if (executionMode == "interactive")
            {
                return ExecuteInteractive(doc, graphPath);
            }

            // Capture elements before execution for change tracking
            var beforeIds = GetAllElementIds(doc);

            // Try to execute using Dynamo automation
            var result = ExecuteViaAutomation(doc, graphPath, inputs);

            if (!result.Success)
            {
                // Fallback: Try via reflection to DynamoRevit
                result = ExecuteViaReflection(doc, graphPath, inputs);
            }

            if (result.Success)
            {
                // Track model changes
                var afterIds = GetAllElementIds(doc);
                var changes = ComputeModelChanges(doc, beforeIds, afterIds);

                if (changes.HasChanges)
                {
                    result = result.WithModelChanges(changes);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail("", "DYNAMO_EXECUTION_ERROR", $"Failed to execute Dynamo graph: {ex.Message}");
        }
    }

    /// <summary>
    /// Execute Dynamo graph using the built-in automation API (Revit 2023+).
    /// </summary>
    private ToolResult ExecuteViaAutomation(Document doc, string graphPath, Dictionary<string, object?> inputs)
    {
        try
        {
            // Try to find DynamoRevitAutomation in loaded assemblies
            var automationType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return Array.Empty<Type>(); }
                })
                .FirstOrDefault(t => t.FullName == "Dynamo.Applications.DynamoRevitAutomation");

            if (automationType == null)
            {
                return ToolResult.Fail("", "DYNAMO_NOT_AVAILABLE", "DynamoRevitAutomation not found");
            }

            // Call RunGraph method
            var runGraphMethod = automationType.GetMethod("RunGraph",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(Document), typeof(string) },
                null);

            if (runGraphMethod == null)
            {
                return ToolResult.Fail("", "DYNAMO_API_ERROR", "RunGraph method not found");
            }

            // Execute the graph
            runGraphMethod.Invoke(null, new object[] { doc, graphPath });

            return ToolResult.Ok("", new Dictionary<string, object?>
            {
                ["message"] = $"Dynamo graph executed successfully: {Path.GetFileName(graphPath)}",
                ["graph_path"] = graphPath,
                ["method"] = "automation",
            });
        }
        catch (TargetInvocationException ex)
        {
            return ToolResult.Fail("", "DYNAMO_EXECUTION_ERROR", ex.InnerException?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail("", "DYNAMO_NOT_AVAILABLE", ex.Message);
        }
    }

    /// <summary>
    /// Execute Dynamo graph using reflection to the DynamoRevit model.
    /// </summary>
    private ToolResult ExecuteViaReflection(Document doc, string graphPath, Dictionary<string, object?> inputs)
    {
        try
        {
            // Try to find the RevitDynamoModel or similar class
            if (!_dynamoChecked)
            {
                _dynamoChecked = true;
                _dynamoRevitType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a =>
                    {
                        try { return a.GetTypes(); }
                        catch { return Array.Empty<Type>(); }
                    })
                    .FirstOrDefault(t =>
                        t.FullName == "Dynamo.Applications.RevitDynamoModel" ||
                        t.FullName == "Dynamo.Applications.DynamoRevit" ||
                        t.Name == "DynamoRevit");
            }

            if (_dynamoRevitType == null)
            {
                return ToolResult.Fail("", "DYNAMO_NOT_LOADED",
                    "Dynamo is not loaded. Please open Dynamo Player or Dynamo editor first, then retry.");
            }

            // Try to get the current Dynamo model instance
            var instanceProperty = _dynamoRevitType.GetProperty("DynamoModel",
                BindingFlags.Public | BindingFlags.Static);

            if (instanceProperty == null)
            {
                // Try alternative approach via DynamoRevitCommandData
                return ExecuteViaCommandData(doc, graphPath, inputs);
            }

            var model = instanceProperty.GetValue(null);
            if (model == null)
            {
                return ToolResult.Fail("", "DYNAMO_NOT_RUNNING",
                    "Dynamo is not running. Please open Dynamo first, then retry.");
            }

            // Open and run the workspace
            var openCommandType = model.GetType().Assembly.GetTypes()
                .FirstOrDefault(t => t.Name == "OpenFileCommand");

            if (openCommandType != null)
            {
                var openCmd = Activator.CreateInstance(openCommandType, graphPath);
                var executeMethod = model.GetType().GetMethod("ExecuteCommand");
                executeMethod?.Invoke(model, new[] { openCmd });

                // Run
                var runMethod = model.GetType().GetMethod("Run");
                runMethod?.Invoke(model, null);
            }

            return ToolResult.Ok("", new Dictionary<string, object?>
            {
                ["message"] = $"Dynamo graph executed: {Path.GetFileName(graphPath)}",
                ["graph_path"] = graphPath,
                ["method"] = "reflection",
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail("", "DYNAMO_REFLECTION_ERROR", ex.Message);
        }
    }

    /// <summary>
    /// Interactive mode: open the Dynamo graph in the Dynamo UI for user interaction.
    /// </summary>
    private ToolResult ExecuteInteractive(Document doc, string graphPath)
    {
        try
        {
            // Find the Dynamo model to open the graph in the UI
            var dynamoRevitType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return Array.Empty<Type>(); }
                })
                .FirstOrDefault(t =>
                    t.FullName == "Dynamo.Applications.RevitDynamoModel" ||
                    t.FullName == "Dynamo.Applications.DynamoRevit" ||
                    t.Name == "DynamoRevit");

            if (dynamoRevitType == null)
            {
                return ToolResult.Fail("", "DYNAMO_NOT_LOADED",
                    "Dynamo is not loaded. Please open Dynamo first, then retry in interactive mode.");
            }

            var instanceProperty = dynamoRevitType.GetProperty("DynamoModel",
                BindingFlags.Public | BindingFlags.Static);

            var model = instanceProperty?.GetValue(null);
            if (model == null)
            {
                return ToolResult.Fail("", "DYNAMO_NOT_RUNNING",
                    "Dynamo is not running. Please open Dynamo first for interactive mode.");
            }

            // Open the graph via OpenFileCommand
            var openCommandType = model.GetType().Assembly.GetTypes()
                .FirstOrDefault(t => t.Name == "OpenFileCommand");

            if (openCommandType != null)
            {
                var openCmd = Activator.CreateInstance(openCommandType, graphPath);
                var executeMethod = model.GetType().GetMethod("ExecuteCommand");
                executeMethod?.Invoke(model, new[] { openCmd });
            }

            return ToolResult.Ok("", new Dictionary<string, object?>
            {
                ["message"] = $"Graph opened in Dynamo UI: {Path.GetFileName(graphPath)}. Interact with Dynamo directly.",
                ["graph_path"] = graphPath,
                ["method"] = "interactive",
                ["execution_mode"] = "interactive",
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail("", "DYNAMO_INTERACTIVE_ERROR",
                $"Failed to open graph in Dynamo UI: {ex.Message}");
        }
    }

    /// <summary>
    /// Alternative approach using DynamoRevitCommandData.
    /// </summary>
    private ToolResult ExecuteViaCommandData(Document doc, string graphPath, Dictionary<string, object?> inputs)
    {
        // This is a placeholder for future implementation
        // Could use the Dynamo Player API or other methods
        return ToolResult.Fail("", "DYNAMO_NOT_AVAILABLE",
            "Dynamo automation is not available. Please ensure Dynamo is installed and loaded in Revit. " +
            "Try opening Dynamo or Dynamo Player first.");
    }

    private static object? GetJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(GetJsonValue).ToList(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => GetJsonValue(p.Value)),
            _ => element.GetRawText()
        };
    }

    private static HashSet<long> GetAllElementIds(Document doc)
    {
        return new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .ToElementIds()
            .Select(id => (long)id.Value)
            .ToHashSet();
    }

    private static ModelChanges ComputeModelChanges(Document doc, HashSet<long> beforeIds, HashSet<long> afterIds)
    {
        var changes = new ModelChanges();

        var createdIds = afterIds.Except(beforeIds);
        foreach (var id in createdIds)
        {
#if REVIT2025 || REVIT2026
            var element = doc.GetElement(new ElementId(id));
#else
            var element = doc.GetElement(new ElementId((int)id));
#endif
            if (element != null)
            {
                changes.Created.Add(new ElementChange
                {
                    ElementId = element.Id.Value,
                    Category = element.Category?.Name,
                    TypeName = element.GetType().Name,
                    Name = element.Name,
                });
            }
        }

        var deletedIds = beforeIds.Except(afterIds);
        foreach (var id in deletedIds)
        {
            changes.Deleted.Add(id);
        }

        return changes;
    }
}
