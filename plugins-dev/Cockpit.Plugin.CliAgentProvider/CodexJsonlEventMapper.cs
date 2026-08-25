using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

// Parses a single JSONL stdout line from `codex exec --json` into typed `PluginSessionEvent`s (#45 phase B1).
// B2 caveat: `item.*` shapes are a best-effort reconstruction from public docs, no captured transcript was
// available; unrecognized `type`/`item_type` values are ignored so schema drift degrades gracefully.
internal static class CodexJsonlEventMapper
{
    // `sessionId` is the caller's currently-known session id (or null before the first `thread.started`),
    // echoed back unchanged unless this line is itself a `thread.started`. Malformed JSON and unrecognized
    // `type`/`item_type` combinations produce zero events rather than throwing.
    public static CodexJsonlMapResult ParseLine(string line, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return new CodexJsonlMapResult([], sessionId);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // A half-written pipe write or a stray non-JSON line — skip it rather than crash the driver.
            return new CodexJsonlMapResult([], sessionId);
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
            {
                return new CodexJsonlMapResult([], sessionId);
            }

            return typeProp.GetString() switch
            {
                "thread.started" => _ParseThreadStarted(root, sessionId),
                "item.started" => new CodexJsonlMapResult(_ParseItemStarted(root, sessionId), sessionId),
                "item.completed" or "item.updated" => new CodexJsonlMapResult(_ParseItemCompleted(root, sessionId), sessionId),
                "turn.completed" => new CodexJsonlMapResult(_ParseTurnCompleted(sessionId), sessionId),
                "turn.failed" => new CodexJsonlMapResult(_ParseTurnFailed(root, sessionId), sessionId),
                "error" => new CodexJsonlMapResult(_ParseTopLevelError(root, sessionId), sessionId),

                // "turn.started" carries no transcript-visible payload the narrow contract needs (the pump
                // already gets its "turn begins" signal from having just spawned the process) — and any other
                // unrecognized type is forward-compat: ignore, don't throw.
                _ => new CodexJsonlMapResult([], sessionId),
            };
        }
    }

    private static CodexJsonlMapResult _ParseThreadStarted(JsonElement root, string? previousSessionId)
    {
        var threadId = root.TryGetProperty("thread_id", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString()
            : previousSessionId;

        PluginSessionEvent[] events = [new PluginSessionInitialized { SessionId = threadId, Tools = [] }];
        return new CodexJsonlMapResult(events, threadId);
    }

    private static IReadOnlyList<PluginSessionEvent> _ParseItemStarted(JsonElement root, string? sessionId)
    {
        if (!root.TryGetProperty("item", out var item))
        {
            return [];
        }

        var itemType = _GetString(item, "item_type");
        var itemId = _GetString(item, "id") ?? string.Empty;

        // Only command executions and MCP tool calls have a request/response shape the narrow contract can
        // represent. reasoning is deliberately not surfaced (no thinking event in the contract); the rest
        // (agent_message/file_change/web_search/todo_list) is a B2 candidate once real field shapes are known.
        return itemType switch
        {
            "command_execution" => [new PluginToolUseRequested { SessionId = sessionId, ToolUseId = itemId, ToolName = "command_execution", InputJson = _ToInputJson(item, "command") }],
            "mcp_tool_call" => [new PluginToolUseRequested { SessionId = sessionId, ToolUseId = itemId, ToolName = _GetString(item, "tool") ?? "mcp_tool_call", InputJson = _ToInputJson(item, "arguments") }],
            _ => [],
        };
    }

    private static IReadOnlyList<PluginSessionEvent> _ParseItemCompleted(JsonElement root, string? sessionId)
    {
        if (!root.TryGetProperty("item", out var item))
        {
            return [];
        }

        var itemType = _GetString(item, "item_type");
        var itemId = _GetString(item, "id") ?? string.Empty;

        switch (itemType)
        {
            case "agent_message":
                // Caveat (design doc §2.3): unknown whether Codex emits token-deltas or whole messages on
                // item.completed. Treated as the latter — one PluginAssistantTextDelta with the full text —
                // so the UI streams per-message instead of per-token if wrong; B2 to verify against a real transcript.
                var text = _GetString(item, "text") ?? string.Empty;
                return [new PluginAssistantTextDelta { SessionId = sessionId, BlockIndex = 0, Text = text }];

            case "command_execution":
                var exitCode = item.TryGetProperty("exit_code", out var exitProp) && exitProp.ValueKind == JsonValueKind.Number
                    ? exitProp.GetInt32()
                    : (int?)null;
                var output = _GetString(item, "aggregated_output") ?? string.Empty;
                return [new PluginToolResult { SessionId = sessionId, ToolUseId = itemId, Content = output, IsError = exitCode.HasValue && exitCode.Value != 0 }];

            case "mcp_tool_call":
                var status = _GetString(item, "status");
                var resultJson = item.TryGetProperty("result", out var resultProp) ? resultProp.GetRawText() : "{}";
                return [new PluginToolResult { SessionId = sessionId, ToolUseId = itemId, Content = resultJson, IsError = status == "failed" }];

            // reasoning/file_change/web_search/todo_list: no narrow-contract event to map to yet (B2 candidate).
            default:
                return [];
        }
    }

    private static IReadOnlyList<PluginSessionEvent> _ParseTurnCompleted(string? sessionId) =>
        [new PluginTurnCompleted { SessionId = sessionId, Subtype = "success", Result = null, IsError = false }];

    private static IReadOnlyList<PluginSessionEvent> _ParseTurnFailed(JsonElement root, string? sessionId)
    {
        var message = root.TryGetProperty("error", out var errorProp)
            ? _GetString(errorProp, "message") ?? errorProp.GetRawText()
            : "Codex reported turn.failed with no error detail.";

        return
        [
            new PluginSessionError { SessionId = sessionId, Message = message },
            new PluginTurnCompleted { SessionId = sessionId, Subtype = "error", Result = null, IsError = true },
        ];
    }

    private static IReadOnlyList<PluginSessionEvent> _ParseTopLevelError(JsonElement root, string? sessionId)
    {
        var message = _GetString(root, "message") ?? "Codex reported a protocol-level error with no message.";

        // A bare protocol-level "error" (outside turn.failed) still ends the in-flight turn from the
        // driver's point of view — surface both, same as turn.failed, rather than leaving the turn hanging.
        return
        [
            new PluginSessionError { SessionId = sessionId, Message = message },
            new PluginTurnCompleted { SessionId = sessionId, Subtype = "error", Result = null, IsError = true },
        ];
    }

    private static string? _GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static string _ToInputJson(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var prop) ? prop.GetRawText() : "{}";
}
