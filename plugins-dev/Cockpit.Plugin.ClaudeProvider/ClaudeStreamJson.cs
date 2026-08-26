using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

// Parses a single JSON-lines stdout line from `claude --output-format stream-json` into zero-or-more events.
// `stream_event` text/thinking deltas carry progressive output, so the `assistant` snapshot's own text/thinking
// is not re-emitted (AC-213); only its tool_use blocks are, since deltas don't carry those.
internal static class ClaudeStreamJson
{
    public static IEnumerable<PluginSessionEvent> ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            yield break;
        }

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var sessionId = root.TryGetProperty("session_id", out var sidProp) ? sidProp.GetString() : null;

        if (!root.TryGetProperty("type", out var typeProp))
        {
            yield break;
        }

        IEnumerable<PluginSessionEvent> events = typeProp.GetString() switch
        {
            "system" => _ParseSystem(root, sessionId),
            "assistant" => _ParseAssistant(root, sessionId),
            "user" => _ParseUser(root, sessionId),
            "stream_event" => _ParseStreamEvent(root, sessionId),
            "result" => [_ParseResult(root, sessionId)],
            _ => [],
        };

        // AC-146: a wire event belonging to a sub-agent carries this alongside session_id, naming the parent
        // Task call's tool_use_id — stamped onto every event via a `with` clone rather than threading the
        // value through every _Parse* method's own construction.
        var parentToolUseId = root.TryGetProperty("parent_tool_use_id", out var parentProp) && parentProp.ValueKind == JsonValueKind.String
            ? parentProp.GetString()
            : null;

        foreach (var evt in events)
        {
            yield return parentToolUseId is null ? evt : evt with { ParentToolUseId = parentToolUseId };
        }
    }

    private static IEnumerable<PluginSessionEvent> _ParseSystem(JsonElement root, string? sessionId) =>
        root.TryGetProperty("subtype", out var st)
            ? st.GetString() switch
            {
                "init" => _ParseInit(root, sessionId),
                "background_tasks_changed" => [_ParseBackgroundTasks(root, sessionId)],
                "task_notification" => _ParseTaskNotification(root, sessionId),
                _ => [],
            }
            : [];

    // The CLI's own ledger of work that outlived its turn (AC-276), restating the complete set every time rather
    // than deltas. An unrecognised `task_type` maps to Unknown rather than being dropped, and the host
    // deliberately acts on neither status nor notification for it — it cannot know which weighing applies.
    private static PluginBackgroundTasksChanged _ParseBackgroundTasks(JsonElement root, string? sessionId)
    {
        var tasks = new List<PluginBackgroundTask>();
        if (root.TryGetProperty("tasks", out var tasksProp) && tasksProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var task in tasksProp.EnumerateArray())
            {
                if (task.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = _String(task, "task_id");
                if (string.IsNullOrEmpty(id))
                {
                    // Without an id there is nothing to count or de-duplicate — a nameless entry would inflate the
                    // set on every restatement.
                    continue;
                }

                var kind = _String(task, "task_type") switch
                {
                    "local_agent" => PluginBackgroundTaskKind.SubAgent,
                    "local_bash" => PluginBackgroundTaskKind.Shell,
                    _ => PluginBackgroundTaskKind.Unknown,
                };

                // _String flattens "absent" to "" — carry that through as null, so a consumer showing the label
                // can tell "no description" from an empty one instead of rendering a blank.
                var description = _String(task, "description");
                tasks.Add(new PluginBackgroundTask(id, kind, string.IsNullOrEmpty(description) ? null : description));
            }
        }

        return new PluginBackgroundTasksChanged { SessionId = sessionId, Tasks = tasks };
    }

    // The CLI's own verdict on a task named by an earlier `background_tasks_changed` (AC-1057, CLI 2.1.246) — the
    // only place completed and failed are told apart; the ledger above only ever says "still there or not". Wire
    // shape is a plain `system` message, not the plain-text `<task-notification>` block an earlier CLI used.
    private static IEnumerable<PluginSessionEvent> _ParseTaskNotification(JsonElement root, string? sessionId)
    {
        var taskId = _String(root, "task_id");
        if (string.IsNullOrEmpty(taskId))
        {
            // Without an id there is nothing to attach the outcome to.
            yield break;
        }

        var status = _String(root, "status") switch
        {
            "completed" => PluginBackgroundTaskStatus.Completed,
            "failed" => PluginBackgroundTaskStatus.Failed,
            _ => PluginBackgroundTaskStatus.Unknown,
        };

        var toolUseId = _String(root, "tool_use_id");
        yield return new PluginBackgroundTaskNotification
        {
            SessionId = sessionId,
            TaskId = taskId,
            ToolUseId = string.IsNullOrEmpty(toolUseId) ? null : toolUseId,
            Status = status,
        };
    }

    private static IEnumerable<PluginSessionEvent> _ParseInit(JsonElement root, string? sessionId)
    {
        var cwd = root.TryGetProperty("cwd", out var cwdProp) ? cwdProp.GetString() : null;
        // The real model the CLI resolved this session to — the only place a session launched with no explicit
        // model (Auto/default) ever states which one it picked (AC-141). _BuildLiveOptions seeds the Model
        // control from the launch option instead, which is null in exactly that case.
        var model = root.TryGetProperty("model", out var modelProp) ? modelProp.GetString() : null;
        var tools = new List<string>();
        if (root.TryGetProperty("tools", out var toolsProp) && toolsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in toolsProp.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String)
                {
                    tools.Add(t.GetString() ?? string.Empty);
                }
            }
        }

        // AC-739: the CLI's own advertised feature set (e.g. "interrupt_cancel_queued_v1", 2.1.231+), so a caller can
        // feature-detect a control-protocol field before sending it to a CLI that has never heard of it.
        var capabilities = new List<string>();
        if (root.TryGetProperty("capabilities", out var capabilitiesProp) && capabilitiesProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in capabilitiesProp.EnumerateArray())
            {
                if (c.ValueKind == JsonValueKind.String)
                {
                    capabilities.Add(c.GetString() ?? string.Empty);
                }
            }
        }

        yield return new PluginSessionInitialized { SessionId = sessionId, Cwd = cwd, Tools = tools, Model = model, Capabilities = capabilities };
    }

    // The assistant snapshot carries complete blocks; both text and thinking are already streamed by the
    // stream_event deltas (--include-partial-messages is always passed), so re-emitting them here would double
    // the rendered content (AC-213). Only tool_use — which the deltas do not carry — is surfaced from the snapshot.
    private static IEnumerable<PluginSessionEvent> _ParseAssistant(JsonElement root, string? sessionId)
    {
        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var blockType))
            {
                continue;
            }

            switch (blockType.GetString())
            {
                case "tool_use":
                    yield return new PluginToolUseRequested
                    {
                        SessionId = sessionId,
                        ToolUseId = _String(block, "id"),
                        ToolName = _String(block, "name"),
                        InputJson = block.TryGetProperty("input", out var input) ? input.GetRawText() : "{}",
                    };
                    break;

                // A "thinking" block is deliberately not re-emitted here: the stream_event thinking_delta path
                // (_ParseStreamEvent) already streamed it incrementally, so emitting the full snapshot too would
                // render the reasoning twice (AC-213).
            }
        }
    }

    private static IEnumerable<PluginSessionEvent> _ParseUser(JsonElement root, string? sessionId)
    {
        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var blockType) || blockType.GetString() != "tool_result")
            {
                continue;
            }

            yield return new PluginToolResult
            {
                SessionId = sessionId,
                ToolUseId = _String(block, "tool_use_id"),
                Content = _ExtractToolResultText(block),
                IsError = block.TryGetProperty("is_error", out var errProp) && errProp.ValueKind == JsonValueKind.True,
            };
        }
    }

    private static string _ExtractToolResultText(JsonElement toolResultBlock)
    {
        if (!toolResultBlock.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("type", out var itemType) && itemType.GetString() == "text"
                    && item.TryGetProperty("text", out var itemText))
                {
                    parts.Add(itemText.GetString() ?? string.Empty);
                }
            }

            return string.Concat(parts);
        }

        return content.GetRawText();
    }

    private static IEnumerable<PluginSessionEvent> _ParseStreamEvent(JsonElement root, string? sessionId)
    {
        if (!root.TryGetProperty("event", out var evt) || !evt.TryGetProperty("type", out var evtType)
            || evtType.GetString() != "content_block_delta"
            || !evt.TryGetProperty("delta", out var delta) || !delta.TryGetProperty("type", out var deltaType))
        {
            yield break;
        }

        var index = evt.TryGetProperty("index", out var idxProp) && idxProp.ValueKind == JsonValueKind.Number ? idxProp.GetInt32() : 0;

        switch (deltaType.GetString())
        {
            case "text_delta":
                yield return new PluginAssistantTextDelta { SessionId = sessionId, BlockIndex = index, Text = _String(delta, "text") };
                break;

            case "thinking_delta":
                yield return new PluginAssistantThinkingDelta { SessionId = sessionId, BlockIndex = index, Thinking = _String(delta, "thinking") };
                break;
        }
    }

    private static PluginTurnCompleted _ParseResult(JsonElement root, string? sessionId) => new()
    {
        SessionId = sessionId,
        Subtype = _String(root, "subtype"),
        Result = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
        IsError = root.TryGetProperty("is_error", out var errProp) && errProp.ValueKind == JsonValueKind.True,
        StopReason = root.TryGetProperty("stop_reason", out var stopProp) && stopProp.ValueKind == JsonValueKind.String ? stopProp.GetString() : null,
        Usage = _ParseUsage(root),
        TotalCostUsd = root.TryGetProperty("total_cost_usd", out var costProp) && costProp.ValueKind == JsonValueKind.Number ? costProp.GetDouble() : null,
        NumTurns = root.TryGetProperty("num_turns", out var turnsProp) && turnsProp.ValueKind == JsonValueKind.Number ? turnsProp.GetInt32() : null,
        Errors = _ParseErrors(root),
    };

    // AC-410: a failed error_during_execution turn carries no "result" — the only place its reason survives.
    // AC-939: an upstream API failure instead reports `subtype: "success"` with `is_error: true` and the failure
    // text in `result`, never `errors[]` — fall back to that text so the reason isn't silently lost.
    private static IReadOnlyList<string>? _ParseErrors(JsonElement root)
    {
        if (root.TryGetProperty("errors", out var errorsProp) && errorsProp.ValueKind == JsonValueKind.Array)
        {
            var errors = new List<string>();
            foreach (var item in errorsProp.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    errors.Add(item.GetString() ?? string.Empty);
                }
            }

            if (errors.Count > 0)
            {
                return errors;
            }
        }

        var isError = root.TryGetProperty("is_error", out var errProp) && errProp.ValueKind == JsonValueKind.True;
        var result = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        return isError && !string.IsNullOrEmpty(result) ? [result] : null;
    }

    private static PluginTokenUsage? _ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new PluginTokenUsage(
            _Int(usage, "input_tokens"),
            _Int(usage, "output_tokens"),
            _Int(usage, "cache_read_input_tokens"),
            _Int(usage, "cache_creation_input_tokens"));
    }

    private static string _String(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() ?? string.Empty : string.Empty;

    private static int _Int(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number ? prop.GetInt32() : 0;
}
