using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.Commands;

/// <summary>
/// Bridges pyrevit.* tool calls to pyRevit's Routes HTTP API. Runs entirely
/// off the Revit API thread so pyRevit's route handler can use the API
/// thread itself when the script touches <c>doc</c> — calling from the API
/// thread deadlocks because pyRevit's handler waits for the same thread.
/// </summary>
public static class PyRevitRouteBridge
{
    /// <summary>pyRevit Routes default port. Configurable in pyRevit Settings.</summary>
    private const int Port = 48884;

    /// <summary>Name of the Revit Orchestrator pyRevit extension's API.</summary>
    private const string ApiName = "orchestrator";

    /// <summary>
    /// Long timeout — Revit scripts can legitimately take minutes. The chat
    /// Cancel button cuts the wait short on the user's signal.
    /// </summary>
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    public static async Task<ToolResult> InvokeAsync(ToolCall call, CancellationToken ct)
    {
        if (call.ToolName != "pyrevit.run_script")
        {
            return ToolResult.Fail(call.CallId, "PYREVIT_UNKNOWN_TOOL",
                $"PyRevitRouteBridge does not handle '{call.ToolName}'. " +
                "Only pyrevit.run_script is bridged via Routes today.");
        }

        var args = call.Args;

        if (!args.TryGetProperty("script_path", out var pathEl)
            || pathEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(pathEl.GetString()))
        {
            return ToolResult.Fail(call.CallId, "INVALID_ARGUMENT",
                "script_path is required");
        }
        var scriptPath = pathEl.GetString()!;

        Dictionary<string, object?> inputs = new();
        if (args.TryGetProperty("arguments", out var argsEl)
            && argsEl.ValueKind == JsonValueKind.Object)
        {
            inputs = JsonElementToDict(argsEl);
        }

        var url = $"http://localhost:{Port}/{ApiName}/run_script/";
        var payload = JsonSerializer.Serialize(new
        {
            script_path = scriptPath,
            inputs,
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Fail(call.CallId, "PYREVIT_ROUTES_UNREACHABLE",
                $"Could not reach pyRevit Routes at {url}: {ex.Message}\n\n" +
                "Likely causes:\n" +
                "  - The orchestrator pyRevit extension is not installed.\n" +
                "    See tools/orchestrator.extension/README.md.\n" +
                "  - pyRevit Routes is disabled. Run:\n" +
                "      pyrevit configs routes enable\n" +
                "    then restart Revit.\n" +
                "  - pyRevit isn't fully loaded yet — click any pyRevit\n" +
                "    ribbon button once and try again.");
        }
        catch (TaskCanceledException)
        {
            return ToolResult.Fail(call.CallId, "PYREVIT_TIMEOUT",
                "pyRevit script did not return within 10 minutes.");
        }

        var body = await resp.Content.ReadAsStringAsync(ct);

        Dictionary<string, object?>? data;
        try
        {
            using var json = JsonDocument.Parse(body);
            data = JsonElementToDict(json.RootElement);
        }
        catch (JsonException)
        {
            return ToolResult.Fail(call.CallId, "PYREVIT_BAD_RESPONSE",
                $"pyRevit Routes returned non-JSON (HTTP {(int)resp.StatusCode}):\n{Truncate(body, 1000)}");
        }

        if (!resp.IsSuccessStatusCode)
        {
            var errMsg = (data.TryGetValue("error", out var e) && e is string s)
                ? s
                : $"HTTP {(int)resp.StatusCode}";
            var trace = data.TryGetValue("traceback", out var t) ? t?.ToString() : null;
            var detail = string.IsNullOrEmpty(trace) ? "" : "\n\n" + trace;
            return ToolResult.Fail(call.CallId, "PYREVIT_SCRIPT_ERROR", errMsg + detail);
        }

        // Soft failure: script returned {"error": "..."}.
        if (data.TryGetValue("error", out var errVal) && errVal is string errStr)
        {
            var ctx = data.Count > 1
                ? "\n\n" + JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true })
                : "";
            return ToolResult.Fail(call.CallId, "SCRIPT_RETURNED_ERROR", errStr + ctx);
        }

        return ToolResult.Ok(call.CallId, data);
    }

    private static Dictionary<string, object?> JsonElementToDict(JsonElement el)
    {
        var dict = new Dictionary<string, object?>();
        if (el.ValueKind != JsonValueKind.Object) return dict;
        foreach (var prop in el.EnumerateObject())
            dict[prop.Name] = JsonElementToObject(prop.Value);
        return dict;
    }

    private static object? JsonElementToObject(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObject).ToList(),
            JsonValueKind.Object => el.EnumerateObject()
                .ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
            _ => el.GetRawText(),
        };
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
