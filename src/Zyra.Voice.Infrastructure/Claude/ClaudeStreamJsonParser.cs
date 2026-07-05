using System.Text.Json;
using Zyra.Voice.Core.Claude;

namespace Zyra.Voice.Infrastructure.Claude;

/// <summary>
/// Parses a single JSON-lines stdout line from <c>claude --output-format stream-json</c>
/// into a typed <see cref="ClaudeSessionEvent"/>, or <see langword="null"/> when the line
/// carries no information F-C1 needs to surface (e.g. a non-text partial delta).
/// </summary>
/// <remarks>
/// Grounded in https://code.claude.com/docs/en/headless.md ("Stream responses" /
/// system-init / api_retry field tables) and
/// https://code.claude.com/docs/en/agent-sdk/streaming-vs-single-mode.md (stream-json
/// message envelope shape). The exact <c>tool_use</c> / <c>tool_result</c> content-block
/// shapes are the well-known Anthropic Messages API content block schema reused verbatim
/// by Claude Code's assistant/user events; F-C1 has no logged-in CLI available to capture
/// a live transcript, so this parser is verified against hand-written fixtures modeled on
/// the documented shapes rather than a captured real session. Treat unrecognized
/// <c>type</c>/<c>subtype</c> combinations as forward-compatible no-ops, not errors.
/// </remarks>
internal static class ClaudeStreamJsonParser
{
    public static ClaudeSessionEvent? TryParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        if (!root.TryGetProperty("type", out var typeProp))
        {
            return null;
        }

        var type = typeProp.GetString();
        var sessionId = root.TryGetProperty("session_id", out var sidProp) ? sidProp.GetString() : null;

        return type switch
        {
            "system" => ParseSystem(root, sessionId),
            "assistant" => ParseAssistant(root, sessionId),
            "user" => ParseUser(root, sessionId),
            "stream_event" => ParseStreamEvent(root, sessionId),
            "result" => ParseResult(root, sessionId),
            _ => null,
        };
    }

    private static ClaudeSessionEvent? ParseSystem(JsonElement root, string? sessionId)
    {
        var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;
        if (subtype != "init")
        {
            return null;
        }

        var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? string.Empty : string.Empty;
        var tools = new List<string>();
        if (root.TryGetProperty("tools", out var toolsProp) && toolsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in toolsProp.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String)
                {
                    tools.Add(t.GetString()!);
                }
            }
        }

        return new SessionInitialized { SessionId = sessionId, Model = model, Tools = tools };
    }

    private static ClaudeSessionEvent? ParseAssistant(JsonElement root, string? sessionId)
    {
        if (!root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // F-C1: surface the first actionable content block. A message with multiple
        // blocks (e.g. text followed by tool_use) will need multiple driver-side events;
        // ClaudeCliSession iterates all blocks itself and calls this per-block via
        // ParseAssistantContentBlock, not this method, for that reason. Kept here only
        // for direct unit-testing convenience against a full assistant message line.
        foreach (var block in content.EnumerateArray())
        {
            var parsed = ParseAssistantContentBlock(block, sessionId);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return null;
    }

    public static ClaudeSessionEvent? ParseAssistantContentBlock(JsonElement block, string? sessionId)
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

    private static ClaudeSessionEvent? ParseUser(JsonElement root, string? sessionId)
    {
        if (!root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return null;
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

            return new ToolResult { SessionId = sessionId, ToolUseId = toolUseId, Content = contentText, IsError = isError };
        }

        return null;
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

    private static ClaudeSessionEvent? ParseStreamEvent(JsonElement root, string? sessionId)
    {
        if (!root.TryGetProperty("event", out var evt) ||
            !evt.TryGetProperty("type", out var evtType))
        {
            return null;
        }

        if (evtType.GetString() != "content_block_delta")
        {
            return null;
        }

        if (!evt.TryGetProperty("delta", out var delta) ||
            !delta.TryGetProperty("type", out var deltaType) ||
            deltaType.GetString() != "text_delta")
        {
            return null;
        }

        var text = delta.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? string.Empty : string.Empty;
        return new AssistantTextDelta { SessionId = sessionId, Text = text };
    }

    private static ClaudeSessionEvent? ParseResult(JsonElement root, string? sessionId)
    {
        var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() ?? string.Empty : string.Empty;
        var result = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        var isError = root.TryGetProperty("is_error", out var errProp) && errProp.ValueKind == JsonValueKind.True;

        return new TurnCompleted { SessionId = sessionId, Subtype = subtype, Result = result, IsError = isError };
    }
}
