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
/// Executes Python scripts (pyRevit-compatible) inside Revit's process
/// using IronPython, with full model change tracking.
/// </summary>
public sealed class RunPythonScriptCommand : IRevitCommand
{
    public string ToolName => "pyrevit.run_script";
    public bool RequiresTransaction => false; // Scripts manage their own transactions

    // Cached IronPython engine types
    private static Assembly? _ironPythonAssembly;
    private static Type? _pythonEngineType;
    private static bool _engineChecked;

    public ToolResult Execute(Document doc, JsonElement args)
    {
        var scriptPath = args.GetProperty("script_path").GetString();
        if (string.IsNullOrEmpty(scriptPath))
        {
            return ToolResult.Fail("", "INVALID_ARGUMENT", "script_path is required");
        }

        if (!File.Exists(scriptPath))
        {
            return ToolResult.Fail("", "FILE_NOT_FOUND", $"Script not found: {scriptPath}");
        }

        // Parse script arguments if provided
        var scriptArgs = new Dictionary<string, object?>();
        if (args.TryGetProperty("arguments", out var argsElement))
        {
            foreach (var prop in argsElement.EnumerateObject())
            {
                scriptArgs[prop.Name] = GetJsonValue(prop.Value);
            }
        }

        // Determine execution mode
        var executionMode = "headless";
        if (args.TryGetProperty("execution_mode", out var modeEl))
            executionMode = modeEl.GetString() ?? "headless";

        try
        {
            // Capture elements before execution
            var beforeIds = GetAllElementIds(doc);

            // Try to execute the script, passing execution mode
            var result = ExecuteWithIronPython(doc, scriptPath, scriptArgs, executionMode);

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
            return ToolResult.Fail("", "SCRIPT_EXECUTION_ERROR", $"Failed to execute script: {ex.Message}");
        }
    }

    /// <summary>
    /// Execute a Python script using IronPython engine.
    /// </summary>
    private ToolResult ExecuteWithIronPython(Document doc, string scriptPath, Dictionary<string, object?> scriptArgs, string executionMode = "headless")
    {
        try
        {
            // Try to find IronPython in loaded assemblies (pyRevit loads it)
            if (!_engineChecked)
            {
                _engineChecked = true;
                FindIronPython();
            }

            if (_pythonEngineType == null)
            {
                // Fallback: try to load from Revit's addins folder or pyRevit
                return ExecuteWithPyRevitEngine(doc, scriptPath, scriptArgs);
            }

            // Create Python engine
            var createEngineMethod = _pythonEngineType.GetMethod("CreateEngine",
                BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);

            if (createEngineMethod == null)
            {
                return ToolResult.Fail("", "IRONPYTHON_ERROR", "Could not find CreateEngine method");
            }

            var engine = createEngineMethod.Invoke(null, null);
            if (engine == null)
            {
                return ToolResult.Fail("", "IRONPYTHON_ERROR", "Failed to create Python engine");
            }

            // Get the engine's type for further method calls
            var engineType = engine.GetType();

            // Create a scope and set up variables
            var createScopeMethod = engineType.GetMethod("CreateScope", Type.EmptyTypes);
            var scope = createScopeMethod?.Invoke(engine, null);

            if (scope != null)
            {
                var scopeType = scope.GetType();
                var setVariableMethod = scopeType.GetMethod("SetVariable",
                    new[] { typeof(string), typeof(object) });

                // Inject common variables like pyRevit does
                setVariableMethod?.Invoke(scope, new object[] { "__revit__", doc.Application });
                setVariableMethod?.Invoke(scope, new object[] { "__doc__", doc });
                setVariableMethod?.Invoke(scope, new object[] { "doc", doc });
                setVariableMethod?.Invoke(scope, new object[] { "uidoc",
                    doc.Application.GetType().GetProperty("ActiveUIDocument")?.GetValue(doc.Application) });

                // Set execution mode so scripts can check it
                setVariableMethod?.Invoke(scope, new object[] { "__execution_mode__", executionMode });

                // Add script arguments
                foreach (var kvp in scriptArgs)
                {
                    setVariableMethod?.Invoke(scope, new object[] { kvp.Key, kvp.Value });
                }
            }

            // Execute the script file
            var executeFileMethod = engineType.GetMethod("ExecuteFile",
                new[] { typeof(string), scope?.GetType() ?? typeof(object) });

            executeFileMethod?.Invoke(engine, new[] { scriptPath, scope });

            // Try to get output from the scope
            string? output = null;
            if (scope != null)
            {
                var tryGetVariableMethod = scope.GetType().GetMethod("TryGetVariable",
                    new[] { typeof(string), typeof(object).MakeByRefType() });

                if (tryGetVariableMethod != null)
                {
                    var args = new object?[] { "__output__", null };
                    var found = (bool)(tryGetVariableMethod.Invoke(scope, args) ?? false);
                    if (found && args[1] != null)
                    {
                        output = args[1].ToString();
                    }
                }
            }

            return ToolResult.Ok("", new Dictionary<string, object?>
            {
                ["message"] = $"Script executed successfully: {Path.GetFileName(scriptPath)}",
                ["script_path"] = scriptPath,
                ["output"] = output,
            });
        }
        catch (TargetInvocationException ex)
        {
            var innerEx = ex.InnerException ?? ex;
            return ToolResult.Fail("", "SCRIPT_ERROR", $"Script error: {innerEx.Message}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail("", "IRONPYTHON_ERROR", ex.Message);
        }
    }

    /// <summary>
    /// Try to use pyRevit's script execution engine if available.
    /// </summary>
    private ToolResult ExecuteWithPyRevitEngine(Document doc, string scriptPath, Dictionary<string, object?> scriptArgs)
    {
        try
        {
            // Look for pyRevit's scripting runtime
            var pyRevitAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name?.Contains("pyRevit") == true);

            if (pyRevitAssembly != null)
            {
                // Try to find pyRevit's script executor
                var executorType = pyRevitAssembly.GetTypes()
                    .FirstOrDefault(t => t.Name.Contains("ScriptExecutor") || t.Name.Contains("ScriptRuntime"));

                if (executorType != null)
                {
                    // Try to execute through pyRevit
                    var executeMethod = executorType.GetMethod("Execute",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);

                    if (executeMethod != null)
                    {
                        // Implementation depends on pyRevit version
                        // This is a placeholder for actual pyRevit integration
                    }
                }
            }

            return ToolResult.Fail("", "PYTHON_NOT_AVAILABLE",
                "IronPython/pyRevit scripting engine not found. " +
                "Ensure pyRevit is installed and loaded, or install IronPython for Revit.");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail("", "PYREVIT_ERROR", ex.Message);
        }
    }

    /// <summary>
    /// Find and cache IronPython assembly and types.
    /// </summary>
    private static void FindIronPython()
    {
        // Look in loaded assemblies first (pyRevit may have loaded it)
        _ironPythonAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "IronPython");

        if (_ironPythonAssembly == null)
        {
            // Try Microsoft.Scripting.dll for alternative Python engines
            var scriptingAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Scripting");

            if (scriptingAssembly != null)
            {
                // IronPython might be available but not loaded yet
                try
                {
                    _ironPythonAssembly = Assembly.Load("IronPython");
                }
                catch
                {
                    // IronPython not available
                }
            }
        }

        if (_ironPythonAssembly != null)
        {
            _pythonEngineType = _ironPythonAssembly.GetType("IronPython.Hosting.Python");
        }
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
