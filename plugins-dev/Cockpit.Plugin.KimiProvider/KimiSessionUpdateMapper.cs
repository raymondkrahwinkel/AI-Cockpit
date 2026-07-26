using System.Collections.Concurrent;
using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.KimiProvider;

/// <summary>
/// Translates a <c>session/update</c> notification's <c>params</c> into zero-or-more <see cref="PluginSessionEvent"/>s
/// (AC-270 sub [c]), discriminated on <c>params.update.sessionUpdate</c> (protocol §4) — the plugin-local mirror of
/// <c>Cockpit.Plugin.ClaudeProvider.ClaudeStreamJson.ParseLine</c>: switches on the discriminator, and an unknown or
/// malformed update yields nothing rather than throwing, so one bad line never kills the notification pump.
/// </summary>
/// <remarks>
/// Not stateless (P1-3): the lazy <c>tool_call</c> create (protocol §4b) carries only a tool name, and the
/// refining <c>tool_call_update</c> — the real title/kind/<c>rawInput</c> — arrives on a later notification.
/// Exactly one <see cref="PluginToolUseRequested"/> must reach the host per toolCallId, at the earliest of: a
/// <c>tool_call</c> that already carries <c>rawInput</c>, the first <c>tool_call_update</c> that does, or a
/// terminal <c>tool_call_update</c> for an id that never got one (with whatever is known by then). One instance
/// is owned per session by <see cref="KimiAcpSessionDriver"/>, which also drives the fourth trigger — a
/// <c>session/request_permission</c> for an id that never got one — through <see cref="EnsureToolUseRequested"/>,
/// since that request arrives outside the <c>session/update</c> stream this class otherwise reads.
/// </remarks>
internal sealed class KimiSessionUpdateMapper
{
    // toolCallId -> the best tool name/rawInput known so far, for an id that has not produced its one
    // PluginToolUseRequested yet. Removed once that event fires; absence here does not by itself mean "already
    // emitted" — see _emittedToolUseRequests, which is the actual source of truth for that.
    private readonly ConcurrentDictionary<string, (string ToolName, string InputJson)> _pendingToolUseRequests = new();

    // toolCallIds that already produced their one PluginToolUseRequested — every later trigger for the same id
    // (a refining tool_call_update, a terminal one, or a permission request) is a no-op for this purpose.
    private readonly ConcurrentDictionary<string, byte> _emittedToolUseRequests = new();

    public KimiSessionUpdateMapResult Map(JsonElement notificationParams)
    {
        if (notificationParams.ValueKind != JsonValueKind.Object
            || !notificationParams.TryGetProperty("update", out var update)
            || update.ValueKind != JsonValueKind.Object
            || !_TryGetString(update, "sessionUpdate", out var discriminator))
        {
            return KimiSessionUpdateMapResult.Empty;
        }

        var sessionId = _TryGetString(notificationParams, "sessionId", out var sid) ? sid : null;

        return discriminator switch
        {
            "agent_message_chunk" => _MapAgentMessageChunk(update, sessionId),
            "agent_thought_chunk" => _MapAgentThoughtChunk(update, sessionId),
            "tool_call" => _MapToolCall(update, sessionId),
            "tool_call_update" => _MapToolCallUpdate(update, sessionId),

            // Cockpit has no plan panel and no slash-command picker in v1 — these carry no surface to render on,
            // so they are dropped on purpose rather than half-mapped to something nobody shows.
            "plan" or "available_commands_update" => KimiSessionUpdateMapResult.Empty,

            "config_option_update" => _MapConfigOptionUpdate(update),
            _ => KimiSessionUpdateMapResult.Empty,
        };
    }

    /// <summary>
    /// Trigger (c) for a <c>session/request_permission</c> (P1-3): if <paramref name="toolCallId"/> never
    /// produced its one <see cref="PluginToolUseRequested"/>, emits it now — using whatever this mapper already
    /// knows about the id, or <paramref name="fallbackToolName"/> if it knows nothing at all — because a
    /// permission card with no matching prior tool-use request has no host-side tool to attach its buttons to
    /// (D3). Returns <see langword="null"/> once the id already has one.
    /// </summary>
    public PluginToolUseRequested? EnsureToolUseRequested(string toolCallId, string? sessionId, string fallbackToolName)
    {
        if (_emittedToolUseRequests.ContainsKey(toolCallId))
        {
            return null;
        }

        var (toolName, inputJson) = _pendingToolUseRequests.TryGetValue(toolCallId, out var known) ? known : (fallbackToolName, "{}");
        return _TryEmitToolUseRequestedOnce(toolCallId, sessionId, toolName, inputJson);
    }

    private KimiSessionUpdateMapResult _MapAgentMessageChunk(JsonElement update, string? sessionId) =>
        _TryGetContentText(update, out var text)
            ? new KimiSessionUpdateMapResult([new PluginAssistantTextDelta { SessionId = sessionId, BlockIndex = 0, Text = text }], null)
            : KimiSessionUpdateMapResult.Empty;

    private KimiSessionUpdateMapResult _MapAgentThoughtChunk(JsonElement update, string? sessionId) =>
        _TryGetContentText(update, out var text)
            ? new KimiSessionUpdateMapResult([new PluginAssistantThinkingDelta { SessionId = sessionId, BlockIndex = 0, Thinking = text }], null)
            : KimiSessionUpdateMapResult.Empty;

    // D4/P1-3, trigger (a): agent-core streams deltas before tool.call.started, so the adapter lazy-creates this
    // first tool_call with status "pending" and only the tool name as title — no rawInput yet. When it does
    // carry rawInput already, this is the earliest trigger and fires PluginToolUseRequested immediately;
    // otherwise the name is remembered until a later trigger ((b) or (c)) decides when the one event fires.
    private KimiSessionUpdateMapResult _MapToolCall(JsonElement update, string? sessionId)
    {
        if (!_TryGetString(update, "toolCallId", out var toolCallId))
        {
            return KimiSessionUpdateMapResult.Empty;
        }

        var toolName = _TryGetString(update, "title", out var title) ? title : "tool";
        var inputJson = update.TryGetProperty("rawInput", out var rawInput) && rawInput.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? rawInput.GetRawText()
            : null;

        if (inputJson is not null)
        {
            return _TryEmitToolUseRequestedOnce(toolCallId, sessionId, toolName, inputJson) is { } emitted
                ? new KimiSessionUpdateMapResult([emitted], null)
                : KimiSessionUpdateMapResult.Empty;
        }

        _pendingToolUseRequests[toolCallId] = (toolName, "{}");
        return KimiSessionUpdateMapResult.Empty;
    }

    private KimiSessionUpdateMapResult _MapToolCallUpdate(JsonElement update, string? sessionId)
    {
        if (!_TryGetString(update, "toolCallId", out var toolCallId) || !_TryGetString(update, "status", out var status))
        {
            return KimiSessionUpdateMapResult.Empty;
        }

        var events = new List<PluginSessionEvent>();
        var hasRawInput = update.TryGetProperty("rawInput", out var rawInput) && rawInput.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        var hasTitle = _TryGetString(update, "title", out var title);

        if (hasRawInput && !_emittedToolUseRequests.ContainsKey(toolCallId))
        {
            // Trigger (b): the refining update — the earliest one carrying rawInput — fires the one event.
            var toolName = hasTitle ? title : _pendingToolUseRequests.TryGetValue(toolCallId, out var known) ? known.ToolName : "tool";
            if (_TryEmitToolUseRequestedOnce(toolCallId, sessionId, toolName, rawInput.GetRawText()) is { } emitted)
            {
                events.Add(emitted);
            }
        }
        else if (hasTitle && _pendingToolUseRequests.TryGetValue(toolCallId, out var pendingSoFar))
        {
            // No rawInput on this particular update, but a refined title arrived — keep it for whichever
            // trigger fires the event later.
            _pendingToolUseRequests[toolCallId] = (title, pendingSoFar.InputJson);
        }

        if (status is not ("completed" or "failed"))
        {
            // "pending"/"in_progress" refine the card but the plugin contract has no "update an already-requested
            // tool call" event — only the terminal states map to PluginToolResult.
            return new KimiSessionUpdateMapResult(events, null);
        }

        // Trigger (c), terminal case: an id that reaches a terminal state without ever producing its one
        // PluginToolUseRequested must still get one now — with whatever is known — or the tool card the host
        // renders has nothing to key its result off (D3).
        if (!_emittedToolUseRequests.ContainsKey(toolCallId))
        {
            var (fallbackName, fallbackInput) = _pendingToolUseRequests.TryGetValue(toolCallId, out var lastKnown) ? lastKnown : ("tool", "{}");
            if (_TryEmitToolUseRequestedOnce(toolCallId, sessionId, fallbackName, fallbackInput) is { } emitted)
            {
                events.Add(emitted);
            }
        }

        // D5: content is REPLACE, not APPEND — this update's content/rawOutput is the whole final payload, not a
        // fragment to accumulate on top of the tool_call's own args preview.
        var content = _ExtractToolResultContent(update);
        events.Add(new PluginToolResult { SessionId = sessionId, ToolUseId = toolCallId, Content = content, IsError = status == "failed" });
        return new KimiSessionUpdateMapResult(events, null);
    }

    private PluginToolUseRequested? _TryEmitToolUseRequestedOnce(string toolCallId, string? sessionId, string toolName, string inputJson)
    {
        if (!_emittedToolUseRequests.TryAdd(toolCallId, 0))
        {
            return null;
        }

        _pendingToolUseRequests.TryRemove(toolCallId, out _);
        return new PluginToolUseRequested { SessionId = sessionId, ToolUseId = toolCallId, ToolName = toolName, InputJson = inputJson };
    }

    private static KimiSessionUpdateMapResult _MapConfigOptionUpdate(JsonElement update) =>
        update.TryGetProperty("configOptions", out var configOptions) && configOptions.ValueKind == JsonValueKind.Array
            ? new KimiSessionUpdateMapResult([], configOptions)
            : KimiSessionUpdateMapResult.Empty;

    private static bool _TryGetContentText(JsonElement update, out string text)
    {
        if (update.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("text", out var textProperty) && textProperty.ValueKind == JsonValueKind.String)
        {
            text = textProperty.GetString() ?? string.Empty;
            return true;
        }

        text = string.Empty;
        return false;
    }

    // The final content array holds blocks shaped like { "type":"content", "content":{ "type":"text", "text":".." } }
    // (the same shape the initial tool_call's args preview uses); their text is concatenated. rawOutput is the
    // fallback when the update carries no such content block.
    private static string _ExtractToolResultContent(JsonElement update)
    {
        if (update.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object
                    && block.TryGetProperty("content", out var inner) && inner.ValueKind == JsonValueKind.Object
                    && inner.TryGetProperty("text", out var textProperty) && textProperty.ValueKind == JsonValueKind.String)
                {
                    parts.Add(textProperty.GetString() ?? string.Empty);
                }
            }

            if (parts.Count > 0)
            {
                return string.Concat(parts);
            }
        }

        return update.TryGetProperty("rawOutput", out var rawOutput) && rawOutput.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? rawOutput.GetRawText()
            : string.Empty;
    }

    private static bool _TryGetString(JsonElement parent, string property, out string value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
