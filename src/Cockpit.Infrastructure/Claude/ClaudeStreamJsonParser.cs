using System.Text.Json;
using Cockpit.Core.Claude;

namespace Cockpit.Infrastructure.Claude;

/// <summary>
/// Parses a single JSON-lines stdout line from <c>claude --output-format stream-json</c>
/// into zero-or-more typed <see cref="ClaudeSessionEvent"/>s.
/// </summary>
/// <remarks>
/// Grounded in a real captured transcript from <c>claude.exe</c> v2.1.197
/// (<c>-p --input-format stream-json --output-format stream-json --verbose --include-partial-messages</c>) —
/// see <c>Memory/Zyra-Voice/StreamJson-Schema.md</c> for the full field-table this parser
/// covers. <c>tool_use</c>/<c>tool_result</c> content-block shapes are the well-known
/// Anthropic Messages API schema reused verbatim by Claude Code, documented but not yet
/// verified against a captured live tool-turn. Unrecognized <c>type</c>/<c>subtype</c>/
/// block-type combinations map to <see cref="UnknownEvent"/> rather than throwing or
/// silently dropping the line, so forward-compat is a parse outcome, not a happy accident.
/// </remarks>
internal static class ClaudeStreamJsonParser
{
    /// <summary>
    /// Parses one stdout line into zero-or-more events. A single line can carry more than one
    /// event (e.g. an <c>assistant</c> snapshot with several content blocks), so callers should
    /// enumerate rather than assume one event per line.
    /// </summary>
    public static IEnumerable<ClaudeSessionEvent> ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            yield break;
        }

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        if (!root.TryGetProperty("type", out var typeProp))
        {
            yield return UnknownEventFrom(root, sessionId: null);
            yield break;
        }

        var type = typeProp.GetString();
        var sessionId = root.TryGetProperty("session_id", out var sidProp) ? sidProp.GetString() : null;

        IEnumerable<ClaudeSessionEvent> events = type switch
        {
            "system" => ParseSystem(root, sessionId),
            "assistant" => ParseAssistantContentBlocks(root, sessionId),
            "user" => ParseUser(root, sessionId),
            "stream_event" => ParseStreamEvent(root, sessionId),
            "rate_limit_event" => ParseRateLimitEvent(root, sessionId),
            "result" => [ParseResult(root, sessionId)],
            _ => [UnknownEventFrom(root, sessionId)],
        };

        foreach (var evt in events)
        {
            yield return evt;
        }
    }

    /// <summary>Back-compat single-event entry point kept for direct unit-testing of one-event lines.</summary>
    public static ClaudeSessionEvent? TryParseLine(string line) => ParseLine(line).FirstOrDefault();

    private static UnknownEvent UnknownEventFrom(JsonElement root, string? sessionId) =>
        new() { SessionId = sessionId, RawJson = root.GetRawText() };

    private static IEnumerable<ClaudeSessionEvent> ParseSystem(JsonElement root, string? sessionId)
    {
        var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;
        var uuid = root.TryGetProperty("uuid", out var uuidProp) ? uuidProp.GetString() : null;

        switch (subtype)
        {
            case "init":
                var cwd = root.TryGetProperty("cwd", out var cwdProp) ? cwdProp.GetString() ?? string.Empty : string.Empty;
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

                yield return new SessionInitialized { SessionId = sessionId, Uuid = uuid, Cwd = cwd, Tools = tools };
                yield break;

            case "post_turn_summary":
                var statusCategory = root.TryGetProperty("status_category", out var scProp) ? scProp.GetString() : null;
                var statusDetail = root.TryGetProperty("status_detail", out var sdProp) ? sdProp.GetString() : null;
                var needsAction = root.TryGetProperty("needs_action", out var naProp) ? naProp.GetString() : null;

                yield return new SessionStatusChanged
                {
                    SessionId = sessionId,
                    Uuid = uuid,
                    StatusCategory = statusCategory,
                    StatusDetail = statusDetail,
                    NeedsAction = needsAction,
                };
                yield break;

            case "notification":
                var text = root.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
                var priority = root.TryGetProperty("priority", out var prioProp) ? prioProp.GetString() : null;

                yield return new SessionStatusChanged
                {
                    SessionId = sessionId,
                    Uuid = uuid,
                    NotificationText = text,
                    NotificationPriority = priority,
                };
                yield break;

            // "status", "thinking_tokens", "hook_started"/"hook_response" and any other
            // system subtype are not (yet) surfaced to the cockpit UI — forward-compat.
            default:
                yield return UnknownEventFrom(root, sessionId);
                yield break;
        }
    }

    private static ClaudeSessionEvent? ParseAssistantContentBlock(JsonElement block, string? sessionId)
    {
        if (!block.TryGetProperty("type", out var blockType))
        {
            return null;
        }

        switch (blockType.GetString())
        {
            case "text":
                var text = block.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                return new AssistantTextCompleted { SessionId = sessionId, Text = text };

            case "thinking":
                var thinking = block.TryGetProperty("thinking", out var thinkProp) ? thinkProp.GetString() ?? string.Empty : string.Empty;
                return new AssistantThinkingDelta { SessionId = sessionId, BlockIndex = 0, Thinking = thinking };

            case "tool_use":
                var id = block.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;
                var name = block.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                var input = block.TryGetProperty("input", out var inputProp)
                    ? inputProp.GetRawText()
                    : "{}";
                return new ToolUseRequested { SessionId = sessionId, ToolUseId = id, ToolName = name, InputJson = input };

            default:
                return null;
        }
    }

    public static IEnumerable<ClaudeSessionEvent> ParseAssistantContentBlocks(JsonElement root, string? sessionId)
    {
        if (!root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var block in content.EnumerateArray())
        {
            var parsed = ParseAssistantContentBlock(block, sessionId);
            if (parsed is not null)
            {
                yield return parsed;
            }
        }
    }

    private static IEnumerable<ClaudeSessionEvent> ParseUser(JsonElement root, string? sessionId)
    {
        if (!root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var blockType) || blockType.GetString() != "tool_result")
            {
                continue;
            }

            var toolUseId = block.TryGetProperty("tool_use_id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;
            var isError = block.TryGetProperty("is_error", out var errProp) && errProp.ValueKind == JsonValueKind.True;
            var contentText = ExtractToolResultText(block);

            yield return new ToolResult { SessionId = sessionId, ToolUseId = toolUseId, Content = contentText, IsError = isError };
        }
    }

    private static string ExtractToolResultText(JsonElement toolResultBlock)
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
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("type", out var itemType) &&
                    itemType.GetString() == "text" &&
                    item.TryGetProperty("text", out var itemText))
                {
                    parts.Add(itemText.GetString() ?? string.Empty);
                }
            }

            return string.Join(string.Empty, parts);
        }

        return content.GetRawText();
    }

    private static IEnumerable<ClaudeSessionEvent> ParseStreamEvent(JsonElement root, string? sessionId)
    {
        var parentToolUseId = root.TryGetProperty("parent_tool_use_id", out var parentProp) &&
                               parentProp.ValueKind == JsonValueKind.String
            ? parentProp.GetString()
            : null;
        var uuid = root.TryGetProperty("uuid", out var uuidProp) ? uuidProp.GetString() : null;

        if (!root.TryGetProperty("event", out var evt) ||
            !evt.TryGetProperty("type", out var evtType))
        {
            yield return UnknownEventFrom(root, sessionId);
            yield break;
        }

        switch (evtType.GetString())
        {
            // message_start/content_block_start/content_block_stop/message_stop carry no
            // transcript-visible payload the cockpit needs yet beyond the lifecycle marker
            // itself (block-start's empty thinking/signature placeholders are re-emitted by
            // the accumulating deltas below); nothing to surface for them today.
            case "message_start":
            case "content_block_start":
            case "content_block_stop":
            case "message_stop":
                yield break;

            case "content_block_delta":
                var index = evt.TryGetProperty("index", out var idxProp) ? idxProp.GetInt32() : 0;

                if (!evt.TryGetProperty("delta", out var delta) || !delta.TryGetProperty("type", out var deltaType))
                {
                    yield return UnknownEventFrom(root, sessionId);
                    yield break;
                }

                switch (deltaType.GetString())
                {
                    case "text_delta":
                        var text = delta.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? string.Empty : string.Empty;
                        yield return new AssistantTextDelta { SessionId = sessionId, ParentToolUseId = parentToolUseId, Uuid = uuid, BlockIndex = index, Text = text };
                        yield break;

                    case "thinking_delta":
                        var thinking = delta.TryGetProperty("thinking", out var thinkProp) ? thinkProp.GetString() ?? string.Empty : string.Empty;
                        yield return new AssistantThinkingDelta { SessionId = sessionId, ParentToolUseId = parentToolUseId, Uuid = uuid, BlockIndex = index, Thinking = thinking };
                        yield break;

                    // signature_delta/input_json_delta carry no transcript-visible text.
                    default:
                        yield break;
                }

            // message_delta only carries stop_reason/usage at end-of-turn; the cockpit already
            // gets its "turn finished" signal from the result event, so nothing to surface here.
            case "message_delta":
                yield break;

            default:
                yield return UnknownEventFrom(root, sessionId);
                yield break;
        }
    }

    private static IEnumerable<ClaudeSessionEvent> ParseRateLimitEvent(JsonElement root, string? sessionId)
    {
        var uuid = root.TryGetProperty("uuid", out var uuidProp) ? uuidProp.GetString() : null;

        if (!root.TryGetProperty("rate_limit_info", out var info))
        {
            yield return UnknownEventFrom(root, sessionId);
            yield break;
        }

        var status = info.TryGetProperty("status", out var statusProp) ? statusProp.GetString() ?? string.Empty : string.Empty;
        var rateLimitType = info.TryGetProperty("rateLimitType", out var typeProp) ? typeProp.GetString() ?? string.Empty : string.Empty;
        var resetsAt = info.TryGetProperty("resetsAt", out var resetsProp) && resetsProp.ValueKind == JsonValueKind.Number
            ? resetsProp.GetInt64()
            : (long?)null;

        yield return new RateLimitInfo
        {
            SessionId = sessionId,
            Uuid = uuid,
            Status = status,
            RateLimitType = rateLimitType,
            ResetsAt = resetsAt,
        };
    }

    private static ClaudeSessionEvent ParseResult(JsonElement root, string? sessionId)
    {
        var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() ?? string.Empty : string.Empty;
        var result = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        var isError = root.TryGetProperty("is_error", out var errProp) && errProp.ValueKind == JsonValueKind.True;
        var stopReason = root.TryGetProperty("stop_reason", out var stopProp) && stopProp.ValueKind == JsonValueKind.String
            ? stopProp.GetString()
            : null;
        var terminalReason = root.TryGetProperty("terminal_reason", out var termProp) && termProp.ValueKind == JsonValueKind.String
            ? termProp.GetString()
            : null;

        return new TurnCompleted
        {
            SessionId = sessionId,
            Subtype = subtype,
            Result = result,
            IsError = isError,
            StopReason = stopReason,
            TerminalReason = terminalReason,
        };
    }
}
