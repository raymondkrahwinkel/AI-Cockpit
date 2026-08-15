using System.Collections.Concurrent;
using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpencodeProvider;

// AC-783: translates a `session/update` notification into events — a copy of KimiSessionUpdateMapper's
// design (malformed input yields nothing, never throws). Not stateless: exactly one PluginToolUseRequested
// must reach the host per toolCallId. `usage_update` is deliberately not handled here — see the driver.
internal sealed class OpencodeSessionUpdateMapper
{
    // How many toolCallIds either map remembers before the oldest is forgotten — exposed for tests.
    internal const int MaxTrackedToolCalls = 4096;

    // toolCallId -> the best tool name/rawInput known so far, for an id that has not produced its one
    // PluginToolUseRequested yet. Removed once that event fires; absence here does not by itself mean "already
    // emitted" — see _emittedToolUseRequests, which is the actual source of truth for that.
    private readonly ConcurrentDictionary<string, (string ToolName, string InputJson)> _pendingToolUseRequests = new();

    // toolCallIds that already produced their one PluginToolUseRequested — every later trigger for the same id
    // (a refining tool_call_update, a terminal one, or a permission request) is a no-op for this purpose.
    private readonly ConcurrentDictionary<string, byte> _emittedToolUseRequests = new();

    // Insertion order for the two maps above, so the oldest id can be dropped once either passes the cap — see
    // KimiSessionUpdateMapper's own remarks for why an unbounded map here is a real memory-growth risk against
    // an untrusted child process.
    private readonly ConcurrentQueue<string> _pendingOrder = new();
    private readonly ConcurrentQueue<string> _emittedOrder = new();

    // How many ids each map currently holds — exposed so a test can prove the cap holds without reaching into
    // the maps themselves.
    internal int TrackedToolCallCountForTests => _pendingToolUseRequests.Count;

    internal int EmittedToolCallCountForTests => _emittedToolUseRequests.Count;

    public OpencodeSessionUpdateMapResult Map(JsonElement notificationParams)
    {
        if (notificationParams.ValueKind != JsonValueKind.Object
            || !notificationParams.TryGetProperty("update", out var update)
            || update.ValueKind != JsonValueKind.Object
            || !_TryGetString(update, "sessionUpdate", out var discriminator))
        {
            return OpencodeSessionUpdateMapResult.Empty;
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
            "plan" or "available_commands_update" => OpencodeSessionUpdateMapResult.Empty,

            "config_option_update" => _MapConfigOptionUpdate(update),

            // usage_update is handled by the driver directly (see its remarks), not here — reaching this
            // switch arm at all would mean the driver's own check for it was bypassed; treated the same as any
            // other unrecognised discriminator: no event, never a throw.
            _ => OpencodeSessionUpdateMapResult.Empty,
        };
    }

    // Trigger (c): if a permission request's toolCallId never produced its one PluginToolUseRequested, emit
    // it now with whatever is known — the card needs a tool to attach its buttons to.
    public PluginToolUseRequested? EnsureToolUseRequested(string toolCallId, string? sessionId, string fallbackToolName)
    {
        if (_emittedToolUseRequests.ContainsKey(toolCallId))
        {
            return null;
        }

        var (toolName, inputJson) = _pendingToolUseRequests.TryGetValue(toolCallId, out var known) ? known : (fallbackToolName, "{}");
        return _TryEmitToolUseRequestedOnce(toolCallId, sessionId, toolName, inputJson);
    }

    private OpencodeSessionUpdateMapResult _MapAgentMessageChunk(JsonElement update, string? sessionId) =>
        _TryGetContentText(update, out var text)
            ? new OpencodeSessionUpdateMapResult([new PluginAssistantTextDelta { SessionId = sessionId, BlockIndex = 0, Text = text }], null)
            : OpencodeSessionUpdateMapResult.Empty;

    private OpencodeSessionUpdateMapResult _MapAgentThoughtChunk(JsonElement update, string? sessionId) =>
        _TryGetContentText(update, out var text)
            ? new OpencodeSessionUpdateMapResult([new PluginAssistantThinkingDelta { SessionId = sessionId, BlockIndex = 0, Thinking = text }], null)
            : OpencodeSessionUpdateMapResult.Empty;

    // Trigger (a): agent-core streams deltas before a tool call's arguments are known (measured live), so a
    // tool_call with rawInput fires immediately; without it, the name is remembered for a later trigger.
    private OpencodeSessionUpdateMapResult _MapToolCall(JsonElement update, string? sessionId)
    {
        if (!_TryGetString(update, "toolCallId", out var toolCallId))
        {
            return OpencodeSessionUpdateMapResult.Empty;
        }

        var toolName = _TryGetString(update, "title", out var title) ? title : "tool";
        var inputJson = update.TryGetProperty("rawInput", out var rawInput) && rawInput.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? rawInput.GetRawText()
            : null;

        if (inputJson is not null)
        {
            return _TryEmitToolUseRequestedOnce(toolCallId, sessionId, toolName, inputJson) is { } emitted
                ? new OpencodeSessionUpdateMapResult([emitted], null)
                : OpencodeSessionUpdateMapResult.Empty;
        }

        _RememberPending(toolCallId, (toolName, "{}"));
        return OpencodeSessionUpdateMapResult.Empty;
    }

    private OpencodeSessionUpdateMapResult _MapToolCallUpdate(JsonElement update, string? sessionId)
    {
        if (!_TryGetString(update, "toolCallId", out var toolCallId) || !_TryGetString(update, "status", out var status))
        {
            return OpencodeSessionUpdateMapResult.Empty;
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
            // trigger fires the event later. The id is already tracked, so this only replaces its value.
            _pendingToolUseRequests[toolCallId] = (title, pendingSoFar.InputJson);
        }

        if (status is not ("completed" or "failed"))
        {
            // "pending"/"in_progress" refine the card but the plugin contract has no "update an already-requested
            // tool call" event — only the terminal states map to PluginToolResult.
            return new OpencodeSessionUpdateMapResult(events, null);
        }

        // Trigger (c), terminal case: an id that reaches a terminal state without ever producing its one
        // PluginToolUseRequested must still get one now — with whatever is known — or the tool card the host
        // renders has nothing to key its result off.
        if (!_emittedToolUseRequests.ContainsKey(toolCallId))
        {
            var (fallbackName, fallbackInput) = _pendingToolUseRequests.TryGetValue(toolCallId, out var lastKnown) ? lastKnown : ("tool", "{}");
            if (_TryEmitToolUseRequestedOnce(toolCallId, sessionId, fallbackName, fallbackInput) is { } emitted)
            {
                events.Add(emitted);
            }
        }

        // Content is REPLACE, not APPEND — this update's content/rawOutput is the whole final payload, not a
        // fragment to accumulate on top of the tool_call's own args preview (measured live: a completed
        // tool_call_update's "content" carries the full "Wrote file successfully." text, not a delta).
        var content = _ExtractToolResultContent(update);
        events.Add(new PluginToolResult { SessionId = sessionId, ToolUseId = toolCallId, Content = content, IsError = status == "failed" });
        return new OpencodeSessionUpdateMapResult(events, null);
    }

    private PluginToolUseRequested? _TryEmitToolUseRequestedOnce(string toolCallId, string? sessionId, string toolName, string inputJson)
    {
        if (!_emittedToolUseRequests.TryAdd(toolCallId, 0))
        {
            return null;
        }

        _emittedOrder.Enqueue(toolCallId);
        _ForgetOldest(_emittedOrder, id => _emittedToolUseRequests.TryRemove(id, out _));

        _pendingToolUseRequests.TryRemove(toolCallId, out _);
        return new PluginToolUseRequested { SessionId = sessionId, ToolUseId = toolCallId, ToolName = toolName, InputJson = inputJson };
    }

    private void _RememberPending(string toolCallId, (string ToolName, string InputJson) known)
    {
        if (!_pendingToolUseRequests.TryAdd(toolCallId, known))
        {
            _pendingToolUseRequests[toolCallId] = known;
            return;
        }

        _pendingOrder.Enqueue(toolCallId);
        _ForgetOldest(_pendingOrder, id => _pendingToolUseRequests.TryRemove(id, out _));
    }

    // Trims on the queue's length rather than the map's: an id that leaves its map early (a pending id that just
    // fired its event) stays queued, so counting the map would let the queue itself grow without end. Bounding
    // the queue bounds both, since every key in a map was enqueued exactly once.
    private static void _ForgetOldest(ConcurrentQueue<string> order, Action<string> forget)
    {
        while (order.Count > MaxTrackedToolCalls && order.TryDequeue(out var oldest))
        {
            forget(oldest);
        }
    }

    private static OpencodeSessionUpdateMapResult _MapConfigOptionUpdate(JsonElement update) =>
        update.TryGetProperty("configOptions", out var configOptions) && configOptions.ValueKind == JsonValueKind.Array
            ? new OpencodeSessionUpdateMapResult([], configOptions)
            : OpencodeSessionUpdateMapResult.Empty;

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
