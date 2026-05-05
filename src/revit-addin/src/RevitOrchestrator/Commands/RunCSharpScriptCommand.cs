using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using RevitOrchestrator.Execution;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.Commands;

/// <summary>
/// Runs a Revit Orchestrator C# tool. The tool is a single .cs file that
/// returns a <c>Dictionary&lt;string, object&gt;</c>. Three globals are
/// pre-defined for the script:
///
///   <c>uiapp</c>  : <see cref="UIApplication"/> — the running Revit UI
///   <c>doc</c>    : <see cref="Document"/>      — the active document
///   <c>inputs</c> : <see cref="Dictionary{TKey,TValue}"/> of LLM args
///
/// The script's last-evaluated expression becomes the tool result. Common
/// imports (<c>System</c>, <c>System.Linq</c>, <c>System.Collections.Generic</c>,
/// <c>Autodesk.Revit.DB</c>, <c>Autodesk.Revit.UI</c>) are pre-imported so a
/// minimal script is just a few lines — see <c>tools/c#/README.md</c>.
///
/// Runs on the Revit API thread (we go through ExternalEvent like every
/// other IRevitCommand) so the script can call into the Revit API directly.
/// </summary>
public sealed class RunCSharpScriptCommand : IRevitCommand
{
    public string ToolName => "csharp.run_script";

    /// <summary>
    /// Tool scripts manage their own transactions if they need to mutate
    /// the model — same convention as Dynamo and pyRevit scripts.
    /// </summary>
    public bool RequiresTransaction => false;

    private static readonly ScriptOptions BaseOptions = ScriptOptions.Default
        .WithReferences(
            typeof(object).Assembly,                     // System.Private.CoreLib
            typeof(Enumerable).Assembly,                 // System.Linq
            typeof(Dictionary<,>).Assembly,              // System.Collections
            typeof(System.Collections.IList).Assembly,   // System.Runtime
            typeof(System.IO.File).Assembly,             // System.IO.FileSystem
            typeof(System.Text.Json.JsonDocument).Assembly,
            typeof(Document).Assembly,                   // RevitAPI
            typeof(UIApplication).Assembly)              // RevitAPIUI
        .WithImports(
            "System",
            "System.Collections.Generic",
            "System.Linq",
            "System.IO",
            "Autodesk.Revit.DB",
            "Autodesk.Revit.UI");

    public ToolResult Execute(Document doc, JsonElement args)
    {
        var scriptPath = args.GetProperty("script_path").GetString()?.Trim();
        if (string.IsNullOrEmpty(scriptPath))
            return ToolResult.Fail("", "INVALID_ARGUMENT", "script_path is required");
        if (!File.Exists(scriptPath))
            return ToolResult.Fail("", "FILE_NOT_FOUND", $"Script not found: {scriptPath}");

        var inputs = ParseInputs(args);
        var uiapp = App.Instance?.UiApplication;
        if (uiapp == null)
            return ToolResult.Fail("", "RUNTIME_ERROR", "UIApplication is not available.");

        string code;
        try { code = File.ReadAllText(scriptPath!); }
        catch (Exception ex)
        {
            return ToolResult.Fail("", "SCRIPT_READ_ERROR", $"Could not read {scriptPath}: {ex.Message}");
        }

        // Allow the script to find sibling .cs files via #load directives.
        var options = BaseOptions.WithFilePath(scriptPath!);

        var beforeIds = GetAllElementIds(doc);
        var globals = new ScriptGlobals { uiapp = uiapp, doc = doc, inputs = inputs };

        ScriptState<object> state;
        try
        {
            // RunAsync is awaitable; we're already on the Revit API thread
            // via ExternalEvent, so block here so the script's API calls
            // run synchronously on this thread.
            state = CSharpScript.RunAsync(code, options, globals).GetAwaiter().GetResult();
        }
        catch (CompilationErrorException cex)
        {
            return ToolResult.Fail("", "SCRIPT_COMPILE_ERROR",
                "C# compile errors:\n  " + string.Join("\n  ", cex.Diagnostics));
        }
        catch (Exception ex)
        {
            return ToolResult.Fail("", "SCRIPT_RUNTIME_ERROR",
                $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}");
        }

        var resultDict = CoerceResult(state.ReturnValue);

        // Treat {"error": "..."} returns as soft failures so the chat sees
        // them as failures with context, just like pyRevit tools do.
        if (resultDict.TryGetValue("error", out var errVal) && errVal is string errStr)
        {
            var ctx = resultDict.Count > 1
                ? "\n\n" + JsonSerializer.Serialize(resultDict, new JsonSerializerOptions { WriteIndented = true })
                : "";
            return ToolResult.Fail("", "SCRIPT_RETURNED_ERROR", errStr + ctx);
        }

        var afterIds = GetAllElementIds(doc);
        var changes = ComputeModelChanges(doc, beforeIds, afterIds);
        var result = ToolResult.Ok("", resultDict);
        if (changes.HasChanges) result = result.WithModelChanges(changes);
        return result;
    }

    /// <summary>
    /// Coerce the script's return value into a JSON-friendly dict. Plain
    /// dicts pass through; any other value is wrapped as <c>{"result": ...}</c>
    /// so callers always see a consistent shape.
    /// </summary>
    private static Dictionary<string, object?> CoerceResult(object? returnValue)
    {
        if (returnValue is Dictionary<string, object?> dn) return dn;
        if (returnValue is Dictionary<string, object> dnn)
            return dnn.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        if (returnValue is IDictionary<string, object?> idn)
            return new Dictionary<string, object?>(idn);
        if (returnValue is IDictionary<string, object> idnn)
            return idnn.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

        return new Dictionary<string, object?> { ["result"] = SummariseRevitObjects(returnValue) };
    }

    /// <summary>
    /// Turn a single object into something JSON-serialisable — Revit
    /// Elements collapse to a small summary, others stringify.
    /// </summary>
    private static object? SummariseRevitObjects(object? value)
    {
        return value switch
        {
            null => null,
            string or bool or int or long or double or float or decimal => value,
            Element e => new Dictionary<string, object?>
            {
                ["element_id"] = e.Id.Value,
                ["name"] = e.Name,
                ["category"] = e.Category?.Name,
                ["type_name"] = e.GetType().Name,
            },
            ElementId eid => eid.Value,
            _ => value.ToString(),
        };
    }

    private static Dictionary<string, object?> ParseInputs(JsonElement args)
    {
        var result = new Dictionary<string, object?>();
        if (args.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in argsEl.EnumerateObject())
                result[prop.Name] = JsonElementToObject(prop.Value);
        }
        return result;
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObject).ToList(),
        JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
        _ => el.GetRawText(),
    };

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
        foreach (var id in afterIds.Except(beforeIds))
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
        foreach (var id in beforeIds.Except(afterIds))
            changes.Deleted.Add(id);
        return changes;
    }

    /// <summary>
    /// Globals exposed to user scripts. Public field names (uiapp, doc,
    /// inputs) are what scripts reference directly — see the README.
    /// </summary>
    public sealed class ScriptGlobals
    {
        public UIApplication uiapp = null!;
        public Document doc = null!;
        public Dictionary<string, object?> inputs = new();
    }
}
