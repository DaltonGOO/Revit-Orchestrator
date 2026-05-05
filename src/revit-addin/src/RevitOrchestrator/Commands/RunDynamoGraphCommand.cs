using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Autodesk.Revit.DB;
using Dynamo.Applications;
using Dynamo.Applications.Models;
using Dynamo.Graph.Nodes;
using Dynamo.Graph.Workspaces;
using Dynamo.Models;
using RevitOrchestrator.Execution;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.Commands;

/// <summary>
/// Executes a Dynamo graph inside Revit using DynamoRevitApp.ExecuteDynamoCommand.
///
/// Two execution paths:
///   * No <c>inputs</c>: fire-and-forget. Opens and runs the graph as-is.
///   * With <c>inputs</c>: opens without running, finds nodes flagged
///     <c>IsSetAsInput</c> by name, sets their <c>Value</c>, runs synchronously,
///     reads back nodes flagged <c>IsSetAsOutput</c>, and returns those values.
/// </summary>
public sealed class RunDynamoGraphCommand : IRevitCommand
{
    private const string LogPrefix = "[Orchestrator/Dynamo]";
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(5);

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

        var hasInputs = args.TryGetProperty("inputs", out var inputsEl)
            && inputsEl.ValueKind == JsonValueKind.Object
            && inputsEl.EnumerateObject().Any();

        try
        {
            EnsureDynamoRevitApp();
            if (_dynRevitApp == null || _execMethod == null)
                return ToolResult.Fail("", "DYNAMO_NOT_LOADED",
                    "Could not create DynamoRevitApp. Is Dynamo for Revit installed and loaded?");

            return hasInputs
                ? ExecuteParameterized(graphPath!, inputsEl, uiApp)
                : ExecuteFireAndForget(graphPath!, uiApp);
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

    // ─────────────────────────────────────────────────────────────────────
    // Path 1: fire-and-forget (no inputs)
    // ─────────────────────────────────────────────────────────────────────

    private ToolResult ExecuteFireAndForget(string graphPath, object uiApp)
    {
        Log($"fire-and-forget: {graphPath}");
        var journal = new Dictionary<string, string>
        {
            { "dynPath", graphPath },
            { "dynShowUI", "False" },
            { "dynAutomation", "True" },
            { "dynPathExecute", "True" },
            { "dynModelShutDown", "False" },
        };
        InvokeDynamoCommand(journal, uiApp);

        return ToolResult.Ok("", new Dictionary<string, object?>
        {
            ["message"] = $"Dynamo graph executed: {Path.GetFileName(graphPath)}",
            ["graph_path"] = graphPath,
            ["inputs_applied"] = new List<string>(),
            ["unknown_inputs"] = new List<string>(),
            ["outputs"] = new Dictionary<string, object?>(),
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // Path 2: parameterized (open → set inputs → run sync → read outputs)
    // ─────────────────────────────────────────────────────────────────────

    private ToolResult ExecuteParameterized(string graphPath, JsonElement inputs, object uiApp)
    {
        Log($"parameterized: {graphPath}");

        // Step 1: Open without executing. dynForceManualRun=True is a defensive
        // measure against a graph that has RunType=Automatic baked into the .dyn.
        var openJournal = new Dictionary<string, string>
        {
            { "dynPath", graphPath },
            { "dynShowUI", "False" },
            { "dynAutomation", "True" },
            { "dynPathExecute", "False" },
            { "dynModelShutDown", "False" },
            { "dynForceManualRun", "True" },
        };
        InvokeDynamoCommand(openJournal, uiApp);
        Log("graph opened");

        // Step 2: Resolve the active RevitDynamoModel
        var model = ResolveDynamoModel();
        if (model == null)
            return ToolResult.Fail("", "DYNAMO_MODEL_UNAVAILABLE",
                "Could not access RevitDynamoModel after opening graph. " +
                "Check DebugView for [Orchestrator/Dynamo] log lines.");
        Log($"model resolved: {model.GetType().FullName}");

        // Step 3: Get the home workspace
        if (model.CurrentWorkspace is not HomeWorkspaceModel workspace)
        {
            var typeName = model.CurrentWorkspace?.GetType().Name ?? "null";
            return ToolResult.Fail("", "DYNAMO_WORKSPACE_UNAVAILABLE",
                $"CurrentWorkspace is not a HomeWorkspaceModel (got {typeName})");
        }
        var nodeList = workspace.Nodes.ToList();
        Log($"workspace nodes: {nodeList.Count}");

        // Step 4: Force RunType to Manual on the workspace's RunSettings so
        // an automatic re-run cannot fire while we mutate input values.
        try
        {
            workspace.RunSettings.RunType = RunType.Manual;
            Log("RunType=Manual");
        }
        catch (Exception ex)
        {
            Log($"WARN: could not set RunType=Manual: {ex.Message}");
        }

        // Step 5: Apply input values
        var setLog = new List<string>();
        var unknownInputs = new List<string>();
        var failedInputs = new Dictionary<string, string>();

        foreach (var prop in inputs.EnumerateObject())
        {
            var node = nodeList.FirstOrDefault(
                n => n.IsSetAsInput && string.Equals(n.Name, prop.Name, StringComparison.Ordinal));
            if (node == null)
            {
                unknownInputs.Add(prop.Name);
                Log($"input '{prop.Name}': no IsSetAsInput node with that Name");
                continue;
            }
            try
            {
                ApplyInputValue(node, prop.Value);
                setLog.Add(prop.Name);
                Log($"input '{prop.Name}' applied to {node.GetType().Name}");
            }
            catch (Exception ex)
            {
                failedInputs[prop.Name] = ex.Message;
                Log($"input '{prop.Name}' FAILED on {node.GetType().Name}: {ex.Message}");
            }
        }

        if (failedInputs.Count > 0)
        {
            var detail = string.Join("; ", failedInputs.Select(kv => $"{kv.Key}: {kv.Value}"));
            return ToolResult.Fail("", "DYNAMO_INPUT_TYPE_UNSUPPORTED",
                $"Could not set {failedInputs.Count} input(s): {detail}");
        }

        // Step 6: Run synchronously, blocking on the EvaluationCompleted event
        using var done = new ManualResetEventSlim(false);
        Exception? evalError = null;
        EventHandler<EvaluationCompletedEventArgs>? handler = null;
        handler = (_, e) =>
        {
            try { evalError = e.Error; } catch { }
            try { workspace.EvaluationCompleted -= handler; } catch { }
            done.Set();
        };
        workspace.EvaluationCompleted += handler;
        workspace.Run();
        Log("Run() called, waiting for EvaluationCompleted");

        if (!done.Wait(RunTimeout))
        {
            try { workspace.EvaluationCompleted -= handler; } catch { }
            return ToolResult.Fail("", "DYNAMO_RUN_TIMEOUT",
                $"Dynamo graph did not complete within {RunTimeout.TotalMinutes:F0} minutes");
        }

        if (evalError != null)
            return ToolResult.Fail("", "DYNAMO_EXECUTION_ERROR",
                $"Graph evaluation error: {evalError.Message}");
        Log("evaluation complete");

        // Step 7: Read outputs (any node flagged IsSetAsOutput)
        var outputs = new Dictionary<string, object?>();
        foreach (var node in nodeList.Where(n => n.IsSetAsOutput))
        {
            var name = string.IsNullOrEmpty(node.Name) ? node.GUID.ToString("N") : node.Name;
            try
            {
                outputs[name] = ReadOutputValue(node);
                Log($"output '{name}' read");
            }
            catch (Exception ex)
            {
                outputs[name] = $"<error reading: {ex.Message}>";
                Log($"output '{name}' read FAILED: {ex.Message}");
            }
        }

        return ToolResult.Ok("", new Dictionary<string, object?>
        {
            ["message"] = $"Dynamo graph executed: {Path.GetFileName(graphPath)}",
            ["graph_path"] = graphPath,
            ["inputs_applied"] = setLog,
            ["unknown_inputs"] = unknownInputs,
            ["outputs"] = outputs,
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // Reaching into Dynamo: model + node value helpers
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Locate the active RevitDynamoModel after the workspace has been opened.
    ///
    /// Dynamo for Revit's API surface for "give me the current model" varies
    /// across versions. We probe a list of likely static accessors on the
    /// DynamoRevit class (and on DynamoRevitApp), then fall back to a full
    /// scan. Each step logs what it found so failures are diagnosable.
    /// </summary>
    private static RevitDynamoModel? ResolveDynamoModel()
    {
        const BindingFlags AllStatic = BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        var dynamoRevitType = ResolveDynamoRevitType();
        if (dynamoRevitType == null)
        {
            Log("ResolveDynamoModel: Dynamo.Applications.DynamoRevit type not found");
            return null;
        }
        Log($"ResolveDynamoModel: {dynamoRevitType.AssemblyQualifiedName}");

        // Probe known accessor names on DynamoRevit
        foreach (var name in new[] { "RevitDynamoModel", "Instance", "DynamoRevitInstance", "Current", "Singleton" })
        {
            if (TryReadStatic(dynamoRevitType, name) is { } val)
            {
                if (val is RevitDynamoModel direct)
                {
                    Log($"  hit: DynamoRevit.{name} → RevitDynamoModel (direct)");
                    return direct;
                }
                if (ExtractModelFromContainer(val) is { } inner)
                {
                    Log($"  hit: DynamoRevit.{name} → container → RevitDynamoModel");
                    return inner;
                }
            }
        }

        // Last-resort scan: every static property/field on DynamoRevit and DynamoRevitApp
        var scanTargets = new[] { dynamoRevitType, _dynRevitApp?.GetType() }
            .Where(t => t != null)
            .Distinct()
            .ToList();

        foreach (var t in scanTargets)
        {
            foreach (var member in t!.GetProperties(AllStatic).Cast<MemberInfo>()
                                      .Concat(t.GetFields(AllStatic)))
            {
                object? val;
                try
                {
                    val = member is PropertyInfo p ? p.GetValue(null) : ((FieldInfo)member).GetValue(null);
                }
                catch { continue; }

                if (val is RevitDynamoModel rm)
                {
                    Log($"  scan: {t.Name}.{member.Name} → RevitDynamoModel");
                    return rm;
                }
                if (ExtractModelFromContainer(val) is { } inner)
                {
                    Log($"  scan: {t.Name}.{member.Name} → container → RevitDynamoModel");
                    return inner;
                }
            }
        }

        Log("ResolveDynamoModel: no path to RevitDynamoModel found");
        return null;
    }

    private static Type? ResolveDynamoRevitType()
    {
        // The DynamoRevit class is in DynamoRevitDS.dll which we reference, but
        // it may not load automatically until Dynamo has been activated once —
        // by the time we reach here, EnsureDynamoRevitApp has done that.
        return Type.GetType("Dynamo.Applications.DynamoRevit, DynamoRevitDS")
               ?? AppDomain.CurrentDomain.GetAssemblies()
                  .SelectMany(a =>
                  {
                      try { return a.GetExportedTypes(); }
                      catch { return Array.Empty<Type>(); }
                  })
                  .FirstOrDefault(t => t.FullName == "Dynamo.Applications.DynamoRevit");
    }

    private static object? TryReadStatic(Type type, string memberName)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        try
        {
            var prop = type.GetProperty(memberName, F);
            if (prop != null && prop.GetGetMethod(true)?.IsStatic == true)
                return prop.GetValue(null);
            var field = type.GetField(memberName, F);
            if (field != null && field.IsStatic)
                return field.GetValue(null);
        }
        catch { /* swallow – we're probing */ }
        return null;
    }

    private static RevitDynamoModel? ExtractModelFromContainer(object? container)
    {
        if (container == null) return null;
        var t = container.GetType();
        foreach (var name in new[] { "RevitDynamoModel", "DynamoModel", "Model" })
        {
            try
            {
                var prop = t.GetProperty(name);
                if (prop?.GetValue(container) is RevitDynamoModel rm) return rm;
                var field = t.GetField(name);
                if (field?.GetValue(container) is RevitDynamoModel rmf) return rmf;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Set an input node's value from a JSON value. Two patterns are supported:
    ///
    /// 1. <b>Settable Value property</b> — used by primitive input nodes such as
    ///    StringInput, IntegerSlider, DoubleSlider, BoolSelector, Filename, etc.
    ///
    /// 2. <b>Dropdown selection (Items + SelectedIndex)</b> — used by
    ///    DSDropDownBase descendants like Categories, Family Types, Element
    ///    Types. The supplied JSON string is matched against the displayed
    ///    <c>Name</c> of each item in <c>Items</c>; a successful match sets
    ///    <c>SelectedIndex</c> and fires <c>OnNodeModified</c> so re-evaluation
    ///    picks up the change.
    /// </summary>
    private static void ApplyInputValue(NodeModel node, JsonElement value)
    {
        var nodeType = node.GetType();

        // Path 1: settable Value property
        var valueProp = nodeType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        if (valueProp != null && valueProp.CanWrite)
        {
            var coerced = CoerceJson(value, valueProp.PropertyType);
            valueProp.SetValue(node, coerced);
            return;
        }

        // Path 2: dropdown selection by item name
        var itemsProp = nodeType.GetProperty("Items", BindingFlags.Public | BindingFlags.Instance);
        var selectedIndexProp = nodeType.GetProperty(
            "SelectedIndex", BindingFlags.Public | BindingFlags.Instance);
        if (itemsProp != null && selectedIndexProp != null && selectedIndexProp.CanWrite
            && itemsProp.GetValue(node) is System.Collections.IList items)
        {
            ApplyDropdownSelection(node, nodeType, items, selectedIndexProp, value);
            return;
        }

        throw new NotSupportedException(
            $"Node type {nodeType.Name} has no recognised input mechanism " +
            "(no settable Value property, no Items+SelectedIndex pair)");
    }

    private static void ApplyDropdownSelection(
        NodeModel node,
        Type nodeType,
        System.Collections.IList items,
        PropertyInfo selectedIndexProp,
        JsonElement value)
    {
        if (items.Count == 0)
            throw new NotSupportedException(
                $"Node type {nodeType.Name}: dropdown items list is empty " +
                "(node may not have populated yet — try opening the graph in " +
                "Dynamo once to seed it, or re-save the graph)");

        var target = value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();
        if (string.IsNullOrEmpty(target))
            throw new NotSupportedException(
                $"Node type {nodeType.Name}: dropdown nodes need a string " +
                "matching one of the option names");

        // Collect names for both matching and the diagnostic message.
        var names = new List<string>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var nameProp = item?.GetType().GetProperty(
                "Name", BindingFlags.Public | BindingFlags.Instance);
            names.Add((nameProp?.GetValue(item) as string) ?? "");
        }

        var matchIndex = names.FindIndex(
            n => string.Equals(n, target, StringComparison.Ordinal));
        if (matchIndex < 0)
            matchIndex = names.FindIndex(
                n => string.Equals(n, target, StringComparison.OrdinalIgnoreCase));

        if (matchIndex < 0)
        {
            var preview = string.Join(", ",
                names.Take(15).Select(n => $"'{n}'"));
            if (names.Count > 15)
                preview += $", … ({names.Count - 15} more)";
            throw new ArgumentException(
                $"'{target}' is not one of {nodeType.Name}'s {names.Count} " +
                $"options. Available: [{preview}]");
        }

        // Setting SelectedIndex to the same value is a no-op (no PropertyChanged
        // fires), so OnNodeModified must be triggered explicitly to mark the
        // node dirty and force re-evaluation.
        selectedIndexProp.SetValue(node, matchIndex);
        var onModified = nodeType.GetMethod(
            "OnNodeModified",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        try
        {
            // The bool overload (forceExecute) is preferred when present.
            if (onModified != null)
            {
                var paramCount = onModified.GetParameters().Length;
                onModified.Invoke(node, paramCount == 0 ? null : new object?[] { true });
            }
        }
        catch
        {
            // Best-effort — graph runs even if the post-set notification fails.
        }
    }

    private static object? CoerceJson(JsonElement value, Type targetType)
    {
        // Handle Nullable<T> by unwrapping
        var underlying = Nullable.GetUnderlyingType(targetType);
        if (underlying != null)
            return value.ValueKind == JsonValueKind.Null ? null : CoerceJson(value, underlying);

        if (targetType == typeof(string))
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => null,
                _ => value.GetRawText(),
            };
        if (targetType == typeof(int)) return value.GetInt32();
        if (targetType == typeof(long)) return value.GetInt64();
        if (targetType == typeof(short)) return value.GetInt16();
        if (targetType == typeof(double)) return value.GetDouble();
        if (targetType == typeof(float)) return (float)value.GetDouble();
        if (targetType == typeof(decimal)) return value.GetDecimal();
        if (targetType == typeof(bool)) return value.GetBoolean();

        throw new NotSupportedException(
            $"Unsupported input value type: {targetType.Name} (JSON kind: {value.ValueKind})");
    }

    /// <summary>
    /// Read a node's <c>CachedValue</c> (a <c>ProtoCore.Mirror.MirrorData</c>)
    /// and convert it to a JSON-serializable value. Reflection-based to keep
    /// our dependency on ProtoCore types loose.
    /// </summary>
    private static object? ReadOutputValue(NodeModel node)
    {
        var cachedProp = typeof(NodeModel).GetProperty("CachedValue", BindingFlags.Public | BindingFlags.Instance);
        var data = cachedProp?.GetValue(node);
        return data == null ? null : ConvertMirrorData(data);
    }

    private static object? ConvertMirrorData(object data)
    {
        var t = data.GetType();
        if ((bool?)t.GetProperty("IsNull")?.GetValue(data) == true)
            return null;

        if ((bool?)t.GetProperty("IsCollection")?.GetValue(data) == true)
        {
            var elementsMethod = t.GetMethod("GetElements");
            if (elementsMethod?.Invoke(data, null) is System.Collections.IEnumerable list)
            {
                var result = new List<object?>();
                foreach (var item in list)
                    result.Add(item == null ? null : ConvertMirrorData(item));
                return result;
            }
        }

        var dataProp = t.GetProperty("Data");
        var raw = dataProp?.GetValue(data);
        return CoerceForJson(raw);
    }

    private static object? CoerceForJson(object? value)
    {
        if (value == null) return null;
        if (value is string or int or long or double or float or bool or decimal or short or byte)
            return value;
        // Revit elements, Dynamo geometry, Lists of complex types -> stringify.
        // The chat-side LLM gets a readable representation rather than a crash.
        return value.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Existing infrastructure: opening the Dynamo UI, resolving the
    // ExecuteDynamoCommand method, and invoking it. Unchanged in behavior.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Open a Dynamo graph in the Dynamo UI for editing/debugging.
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
            invokeArgs = methodParams[0].ParameterType == typeof(Dictionary<string, string>)
                ? new object[] { journalData }
                : new object[] { uiApp };
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

    private static void EnsureDynamoRevitApp()
    {
        if (_dynRevitApp != null && _execMethod != null) return;

        _dynRevitApp = null;
        _execMethod = null;

        var handle = Activator.CreateInstance("DynamoRevitDS", "Dynamo.Applications.DynamoRevitApp");
        if (handle == null)
            throw new FileNotFoundException("Could not create DynamoRevitApp. Is Dynamo for Revit installed?");

        var app = handle.Unwrap();
        if (app == null)
            throw new FileNotFoundException("Could not unwrap DynamoRevitApp instance.");

        var allMethods = app.GetType().GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        var method = allMethods
            .FirstOrDefault(m => m.Name == "ExecuteDynamoCommand"
                && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType == typeof(Dictionary<string, string>));
        method ??= allMethods.FirstOrDefault(m => m.Name == "ExecuteDynamoCommand");
        method ??= allMethods.FirstOrDefault(m => m.Name.Contains("Execute") && m.Name.Contains("Dynamo"));

        if (method == null)
        {
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

    private static void Log(string msg)
    {
        System.Diagnostics.Debug.WriteLine($"{LogPrefix} {msg}");
    }
}
