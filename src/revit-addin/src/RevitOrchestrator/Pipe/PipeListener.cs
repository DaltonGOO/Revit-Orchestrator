using System.Text.Json;
using RevitOrchestrator.Capture;
using RevitOrchestrator.Execution;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.Pipe;

/// <summary>
/// Background listener that reads messages from the pipe and dispatches tool calls.
/// Implements reconnection with exponential backoff.
/// </summary>
public sealed class PipeListener : IDisposable
{
    private readonly string _pipeName;
    private readonly CommandQueue _commandQueue;
    private PipeClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public event Action<string>? OnStatusChanged;
    public event Action<PipeMessage>? OnMessageReceived;
    public event Action<string>? OnChatResponse;
    public event Action<string>? OnChatStatus;
    public event Action<JsonElement>? OnToolListResponse;
    public event Action<JsonElement>? OnToolAddResponse;
    public event Action<JsonElement>? OnToolDeleteResponse;
    public event Action<JsonElement>? OnExecutionEvent;
    public event Action<JsonElement>? OnWorkflowSaveResponse;
    public event Action<JsonElement>? OnWorkflowSuggestion;
    public event Action<JsonElement>? OnWorkflowLoadResponse;
    public event Action<JsonElement>? OnWorkflowReviewResponse;
    public event Action<JsonElement>? OnWorkflowTestResponse;
    public event Action<JsonElement>? OnWorkflowRunResponse;
    public event Action<JsonElement>? OnToolRunResponse;
    public event Action<JsonElement>? OnToolLoadResponse;
    public event Action<JsonElement>? OnToolUpdateResponse;
    public event Action<JsonElement>? OnSettingsResponse;

    /// <summary>
    /// Fired when an approval request is received from the Python server.
    /// The handler should show a dialog and return the result.
    /// </summary>
    public Func<ApprovalRequest, Task<bool>>? OnApprovalRequest;

    /// <summary>
    /// Function to gather current Revit run context for status queries.
    /// </summary>
    public Func<RunContext>? GetRunContext;

    /// <summary>
    /// Function to capture the active viewport as PNG bytes.
    /// Called from background thread — implementation must handle Revit API
    /// thread marshalling if needed (e.g. reading doc properties is generally safe).
    /// </summary>
    public Func<byte[]?>? CaptureScreenshot;

    /// <summary>
    /// Function to build element identity data for given element IDs.
    /// Called from background thread — implementation must handle Revit API
    /// thread marshalling if needed.
    /// </summary>
    public Func<long[], List<Dictionary<string, object?>>>? GetElementIdentities;

    public PipeListener(string pipeName, CommandQueue commandQueue)
    {
        _pipeName = pipeName;
        _commandQueue = commandQueue;
    }

    public bool IsConnected => _client?.IsConnected ?? false;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _client?.Dispose();
        _client = null;
    }

    /// <summary>
    /// Send a chat message to the Python server for LLM processing.
    /// </summary>
    public async Task SendChatMessageAsync(string content)
    {
        if (_client is null || !_client.IsConnected)
            throw new InvalidOperationException("Pipe is not connected");

        var message = PipeMessage.Create("chat_message", new { content });
        await _client.SendAsync(message);
    }

    /// <summary>
    /// Request the list of registered tools from the Python server.
    /// </summary>
    public async Task RequestToolListAsync()
    {
        if (_client is null || !_client.IsConnected)
            throw new InvalidOperationException("Pipe is not connected");

        var message = PipeMessage.Create("tool_list_request", new { });
        await _client.SendAsync(message);
    }

    /// <summary>
    /// Request that the Python server parse a file and add it as a new tool.
    /// </summary>
    public async Task AddToolAsync(string filePath, object? metadata = null)
    {
        if (_client is null || !_client.IsConnected)
            throw new InvalidOperationException("Pipe is not connected");

        var message = PipeMessage.Create("tool_add_request", new { file_path = filePath, metadata });
        await _client.SendAsync(message);
    }

    /// <summary>
    /// Request that the Python server delete a tool by name.
    /// </summary>
    public async Task DeleteToolAsync(string toolName)
    {
        if (_client is null || !_client.IsConnected)
            throw new InvalidOperationException("Pipe is not connected");

        var message = PipeMessage.Create("tool_delete_request", new { tool_name = toolName });
        await _client.SendAsync(message);
    }

    /// <summary>
    /// Request that the Python server save a workflow from execution history.
    /// </summary>
    public async Task SaveWorkflowAsync(string name, string description, List<WorkflowStep> steps,
        Models.WorkflowDefinition? definition = null)
    {
        if (_client is null || !_client.IsConnected)
            throw new InvalidOperationException("Pipe is not connected");

        var stepData = steps.Select(s => new Dictionary<string, object?>
        {
            ["tool_name"] = s.ToolName,
            ["args"] = s.Args,
            ["bindings"] = s.Bindings,
            ["guard"] = s.Guard,
            ["on_failure"] = s.OnFailure,
            ["max_retries"] = s.MaxRetries,
            ["timeout_ms"] = s.TimeoutMs,
        }).ToList();

        var payload = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["description"] = description,
            ["steps"] = stepData,
        };

        // Include original name for rename detection
        if (definition != null)
        {
            payload["original_name"] = definition.OriginalName ?? "";
        }

        // Include governance metadata if provided
        if (definition != null)
        {
            payload["author"] = definition.Author;
            payload["tags"] = definition.Tags;
            payload["version"] = definition.Version;
            payload["permission_mode"] = definition.PermissionMode;
            payload["approval_required"] = definition.ApprovalRequired;
            payload["side_effects"] = definition.SideEffects;
            if (definition.WasLlmReviewed)
            {
                payload["llm_review"] = new Dictionary<string, object?>
                {
                    ["summary"] = definition.LlmReviewSummary,
                    ["flags"] = definition.LlmReviewFlags,
                };
            }

            // Send promoted parameters so the server populates parameters.properties
            if (definition.Parameters.Count > 0)
            {
                payload["promoted_parameters"] = definition.Parameters.ToDictionary(
                    kvp => kvp.Key,
                    kvp => (object)new Dictionary<string, object?>
                    {
                        ["type"] = kvp.Value.Type,
                        ["description"] = kvp.Value.Description,
                        ["default"] = kvp.Value.DefaultValue,
                    });
            }
        }

        var message = PipeMessage.Create("workflow_save_request", payload);
        await _client.SendAsync(message);
    }

    /// <summary>
    /// Request a workflow definition from the Python server for editing.
    /// </summary>
    public async Task LoadWorkflowAsync(string workflowName)
    {
        if (_client is null || !_client.IsConnected)
            throw new InvalidOperationException("Pipe is not connected");

        var message = PipeMessage.Create("workflow_load_request", new { workflow_name = workflowName });
        await _client.SendAsync(message);
    }

    /// <summary>
    /// Request an LLM review of a workflow before saving.
    /// </summary>
    public async Task ReviewWorkflowAsync(List<WorkflowStep> steps, string description)
    {
        if (_client is null || !_client.IsConnected)
            throw new InvalidOperationException("Pipe is not connected");

        var stepData = steps.Select(s => new Dictionary<string, object?>
        {
            ["tool_name"] = s.ToolName,
            ["args"] = s.Args,
            ["bindings"] = s.Bindings,
            ["guard"] = s.Guard,
            ["on_failure"] = s.OnFailure,
        }).ToList();

        var message = PipeMessage.Create("workflow_review_request", new
        {
            steps = stepData,
            description
        });
        await _client.SendAsync(message);
    }

    /// <summary>
    /// Request the Python server to test-run a workflow without saving it.
    /// </summary>
    public async Task TestWorkflowAsync(List<WorkflowStep> steps, string description)
    {
        if (_client is null || !_client.IsConnected)
            throw new InvalidOperationException("Pipe is not connected");

        var stepData = steps.Select((s, i) => new Dictionary<string, object?>
        {
            ["id"] = string.IsNullOrEmpty(s.Id) ? $"step{i + 1}" : s.Id,
            ["tool"] = s.ToolName,
            ["args"] = s.Args,
            ["bindings"] = s.Bindings,
            ["guard"] = s.Guard,
            ["on_failure"] = s.OnFailure,
            ["max_retries"] = s.MaxRetries,
            ["timeout_ms"] = s.TimeoutMs,
        }).ToList();

        var msg = PipeMessage.Create("workflow_test_request", new
        {
            steps = stepData,
            description
        });
        await _client.SendAsync(msg);
    }

    /// <summary>
    /// Request the Python server to execute a saved workflow with input arguments.
    /// </summary>
    public async Task RunWorkflowAsync(string workflowName, Dictionary<string, object?> inputArgs)
    {
        if (_client is null || !_client.IsConnected)
            throw new InvalidOperationException("Pipe is not connected");

        var msg = PipeMessage.Create("workflow_run_request", new
        {
            workflow_name = workflowName,
            input_args = inputArgs,
        });
        await _client.SendAsync(msg);
    }

    /// <summary>
    /// Request the Python server to execute any tool with input arguments.
    /// </summary>
    public async Task RunToolAsync(string toolName, Dictionary<string, object?> inputArgs, string executionMode = "headless")
    {
        if (_client is null || !_client.IsConnected)
            throw new InvalidOperationException("Pipe is not connected");

        var msg = PipeMessage.Create("tool_run_request", new
        {
            tool_name = toolName,
            input_args = inputArgs,
            execution_mode = executionMode,
        });
        await _client.SendAsync(msg);
    }

    /// <summary>
    /// Request the full tool definition from the Python server for editing.
    /// </summary>
    public async Task LoadToolAsync(string toolName)
    {
        if (_client is null || !_client.IsConnected)
            throw new InvalidOperationException("Pipe is not connected");

        var msg = PipeMessage.Create("tool_load_request", new { tool_name = toolName });
        await _client.SendAsync(msg);
    }

    /// <summary>
    /// Request the server configuration and settings info.
    /// </summary>
    public async Task RequestSettingsAsync()
    {
        if (_client is null || !_client.IsConnected)
            throw new InvalidOperationException("Pipe is not connected");

        var message = PipeMessage.Create("settings_request", new { });
        await _client.SendAsync(message);
    }

    /// <summary>
    /// Send an updated tool definition to the Python server for validation and saving.
    /// </summary>
    public async Task UpdateToolAsync(string toolName, Dictionary<string, object?> definition)
    {
        if (_client is null || !_client.IsConnected)
            throw new InvalidOperationException("Pipe is not connected");

        var msg = PipeMessage.Create("tool_update_request", new
        {
            tool_name = toolName,
            definition,
        });
        await _client.SendAsync(msg);
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        var backoffMs = 1000;
        const int maxBackoffMs = 30_000;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                OnStatusChanged?.Invoke("Connecting...");
                _client = new PipeClient(_pipeName);
                await _client.ConnectAsync(ct);
                OnStatusChanged?.Invoke("Connected");
                backoffMs = 1000; // Reset backoff on successful connection

                await ReadLoopAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Disconnected: {ex.Message}");
                _client?.Dispose();
                _client = null;
            }

            if (!ct.IsCancellationRequested)
            {
                OnStatusChanged?.Invoke($"Reconnecting in {backoffMs / 1000}s...");
                await Task.Delay(backoffMs, ct);
                backoffMs = Math.Min(backoffMs * 2, maxBackoffMs);
            }
        }

        OnStatusChanged?.Invoke("Stopped");
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && (_client?.IsConnected ?? false))
        {
            var message = await _client!.ReadAsync(ct);
            OnMessageReceived?.Invoke(message);

            switch (message.Type)
            {
                case "ping":
                    await _client.SendAsync(PipeMessage.Pong(), ct);
                    break;

                case "tool_call":
                    await HandleToolCallAsync(message, ct);
                    break;

                case "chat_response":
                    HandleChatResponse(message);
                    break;

                case "chat_status":
                    HandleChatStatusMessage(message);
                    break;

                case "tool_list_response":
                    OnToolListResponse?.Invoke(message.Payload);
                    break;

                case "tool_add_response":
                    OnToolAddResponse?.Invoke(message.Payload);
                    break;

                case "tool_delete_response":
                    OnToolDeleteResponse?.Invoke(message.Payload);
                    break;

                case "execution_event":
                    OnExecutionEvent?.Invoke(message.Payload);
                    break;

                case "workflow_save_response":
                    OnWorkflowSaveResponse?.Invoke(message.Payload);
                    break;

                case "status_query":
                    await HandleStatusQueryAsync(message);
                    break;

                case "approval_request":
                    await HandleApprovalRequestAsync(message);
                    break;

                case "workflow_suggestion":
                    OnWorkflowSuggestion?.Invoke(message.Payload);
                    break;

                case "workflow_load_response":
                    OnWorkflowLoadResponse?.Invoke(message.Payload);
                    break;

                case "workflow_review_response":
                    OnWorkflowReviewResponse?.Invoke(message.Payload);
                    break;

                case "workflow_test_response":
                    OnWorkflowTestResponse?.Invoke(message.Payload);
                    break;

                case "workflow_run_response":
                    OnWorkflowRunResponse?.Invoke(message.Payload);
                    break;

                case "tool_run_response":
                    OnToolRunResponse?.Invoke(message.Payload);
                    break;

                case "tool_load_response":
                    OnToolLoadResponse?.Invoke(message.Payload);
                    break;

                case "tool_update_response":
                    OnToolUpdateResponse?.Invoke(message.Payload);
                    break;

                case "settings_response":
                    OnSettingsResponse?.Invoke(message.Payload);
                    break;

                case "screenshot_request":
                    await HandleScreenshotRequestAsync(message);
                    break;

                case "context_request":
                    await HandleContextRequestAsync(message);
                    break;

                case "element_identity_request":
                    await HandleElementIdentityRequestAsync(message);
                    break;
            }
        }
    }

    private async Task HandleToolCallAsync(PipeMessage message, CancellationToken ct)
    {
        var toolCall = JsonSerializer.Deserialize<ToolCall>(message.Payload.GetRawText());
        if (toolCall is null) return;

        toolCall.CallId = message.Id;

        // Enqueue and wait for the result via ExternalEvent
        var result = await _commandQueue.EnqueueAsync(toolCall, ct);

        // Send result back
        var response = PipeMessage.Create("tool_result", result);
        await _client!.SendAsync(response, ct);
    }

    private async Task HandleStatusQueryAsync(PipeMessage message)
    {
        var callId = message.Id;
        var context = GetRunContext?.Invoke() ?? new RunContext();

        var response = PipeMessage.Create("status_response", new
        {
            call_id = callId,
            revit_version = context.RevitVersion,
            document_title = context.DocumentTitle,
            document_guid = context.DocumentGuid,
            is_worksharing = context.IsWorksharingEnabled,
            active_view_type = context.ActiveViewType,
            user_name = context.UserName,
        });

        if (_client != null)
            await _client.SendAsync(response);
    }

    private async Task HandleApprovalRequestAsync(PipeMessage message)
    {
        var payload = message.Payload;
        var request = new ApprovalRequest
        {
            CallId = payload.TryGetProperty("call_id", out var callIdEl) ? callIdEl.GetString() ?? "" : "",
            ToolName = payload.TryGetProperty("tool_name", out var toolEl) ? toolEl.GetString() ?? "" : "",
            Args = payload.TryGetProperty("args", out var argsEl) ? argsEl : default,
            SideEffects = payload.TryGetProperty("side_effects", out var effectsEl)
                ? effectsEl.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
                : Array.Empty<string>(),
        };

        bool approved = false;
        if (OnApprovalRequest != null)
        {
            approved = await OnApprovalRequest(request);
        }

        var response = PipeMessage.Create("approval_response", new
        {
            approved,
            call_id = request.CallId,
        });

        if (_client != null)
            await _client.SendAsync(response);
    }

    private async Task HandleScreenshotRequestAsync(PipeMessage message)
    {
        var callId = message.Id;
        byte[]? imageBytes = null;

        try
        {
            imageBytes = CaptureScreenshot?.Invoke();
        }
        catch { }

        var payload = new Dictionary<string, object?>
        {
            ["call_id"] = callId,
            ["success"] = imageBytes != null,
            ["image_base64"] = imageBytes != null ? Convert.ToBase64String(imageBytes) : null,
            ["mime_type"] = "image/png",
        };

        var response = PipeMessage.Create("screenshot_response", payload);
        if (_client != null)
            await _client.SendAsync(response);
    }

    private async Task HandleContextRequestAsync(PipeMessage message)
    {
        var callId = message.Id;
        var context = GetRunContext?.Invoke() ?? new RunContext();

        var response = PipeMessage.Create("context_response", new
        {
            call_id = callId,
            revit_version = context.RevitVersion,
            document_title = context.DocumentTitle,
            document_guid = context.DocumentGuid,
            is_worksharing = context.IsWorksharingEnabled,
            active_view_type = context.ActiveViewType,
            user_name = context.UserName,
        });

        if (_client != null)
            await _client.SendAsync(response);
    }

    private async Task HandleElementIdentityRequestAsync(PipeMessage message)
    {
        var callId = message.Id;
        var payload = message.Payload;

        var elementIds = Array.Empty<long>();
        if (payload.TryGetProperty("element_ids", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
        {
            elementIds = idsEl.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Number)
                .Select(e => e.GetInt64())
                .ToArray();
        }

        var identities = new List<Dictionary<string, object?>>();
        try
        {
            if (GetElementIdentities != null)
                identities = GetElementIdentities(elementIds);
        }
        catch { }

        var response = PipeMessage.Create("element_identity_response", new
        {
            call_id = callId,
            success = true,
            identities,
        });

        if (_client != null)
            await _client.SendAsync(response);
    }

    private void HandleChatResponse(PipeMessage message)
    {
        try
        {
            var payload = message.Payload;
            var isFinal = payload.TryGetProperty("is_final", out var finalEl) && finalEl.GetBoolean();
            if (isFinal && payload.TryGetProperty("content", out var contentEl))
            {
                OnChatResponse?.Invoke(contentEl.GetString() ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            OnChatResponse?.Invoke($"Error parsing response: {ex.Message}");
        }
    }

    private void HandleChatStatusMessage(PipeMessage message)
    {
        try
        {
            var payload = message.Payload;
            if (payload.TryGetProperty("status", out var statusEl))
            {
                OnChatStatus?.Invoke(statusEl.GetString() ?? string.Empty);
            }
        }
        catch
        {
            // Ignore malformed status messages
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}

/// <summary>
/// Represents a step in a workflow to be saved.
/// </summary>
public class WorkflowStep
{
    public string Id { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public Dictionary<string, object?> Args { get; set; } = new();
    public Dictionary<string, string>? Bindings { get; set; }
    public string? Guard { get; set; }
    public string OnFailure { get; set; } = "stop";
    public int MaxRetries { get; set; } = 0;
    public int? TimeoutMs { get; set; }
}
