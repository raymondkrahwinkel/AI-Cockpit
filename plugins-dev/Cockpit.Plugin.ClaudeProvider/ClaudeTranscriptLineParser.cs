using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

// Parses one line of a `claude` session's live JSONL transcript, extracting the turn-activity, usage and
// background-work signals the host's TTY status dot (#39) needs. The plugin owns this format knowledge —
// the core must know nothing of Claude's JSONL shape.
internal static class ClaudeTranscriptLineParser
{
    // Reads the CLI's own count of sub-agents still running, written on the turn_duration line closing every
    // turn (AC-276); absence means zero. A count the provider states, not one this reader keeps, so a missed
    // line costs one stale reading rather than desynchronising a ledger. Sub-agents only; shells are separate.
    public static bool TryReadPendingSubAgentCount(string transcriptLine, out int count)
    {
        count = 0;
        if (string.IsNullOrWhiteSpace(transcriptLine))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(transcriptLine);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.GetString() != "system"
                || !root.TryGetProperty("subtype", out var subtype)
                || subtype.GetString() != "turn_duration")
            {
                return false;
            }

            count = root.TryGetProperty("pendingBackgroundAgentCount", out var pending)
                && pending.ValueKind == JsonValueKind.Number
                    ? pending.GetInt32()
                    : 0;
            return true;
        }
        catch (JsonException)
        {
            // A tail read landing mid-write — transient, not an error to surface.
            return false;
        }
    }

    // Reads a backgrounded shell starting or ending (AC-276), keyed on the `tool_use` id both ends carry.
    // Unlike the sub-agent count above, there is no provider-stated total for shells, so this one *is* a ledger:
    // a missed end leaves a shell counted forever — bounded, since that only withholds a notification, not the status.
    public static bool TryReadBackgroundShellTransition(string transcriptLine, out string toolUseId, out bool started)
    {
        toolUseId = string.Empty;
        started = false;
        if (string.IsNullOrWhiteSpace(transcriptLine))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(transcriptLine);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type))
            {
                return false;
            }

            switch (type.GetString())
            {
                case "assistant" when root.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.Object
                    && message.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Array:
                    foreach (var block in content.EnumerateArray())
                    {
                        if (block.ValueKind != JsonValueKind.Object
                            || !block.TryGetProperty("type", out var blockType)
                            || blockType.GetString() != "tool_use"
                            || !block.TryGetProperty("name", out var name)
                            || name.GetString() != "Bash"
                            || !block.TryGetProperty("input", out var input)
                            || input.ValueKind != JsonValueKind.Object
                            || !input.TryGetProperty("run_in_background", out var background)
                            || background.ValueKind != JsonValueKind.True
                            || !block.TryGetProperty("id", out var id)
                            || id.GetString() is not { Length: > 0 } startedId)
                        {
                            continue;
                        }

                        toolUseId = startedId;
                        started = true;
                        return true;
                    }

                    return false;

                case "queue-operation" when root.TryGetProperty("content", out var notification)
                    && notification.GetString() is { } text
                    && _TryReadNotifiedToolUseId(text, out var endedId):
                    toolUseId = endedId;
                    started = false;
                    return true;

                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Pulls the `&lt;tool-use-id&gt;` out of the CLI's `&lt;task-notification&gt;` block. Deliberately a
    // substring read and not an XML parse: this is a text payload the CLI composes for the model, not a contract —
    // so it is matched narrowly enough to be wrong loudly (no id found ⇒ no transition) rather than to guess.
    private static bool _TryReadNotifiedToolUseId(string content, out string toolUseId)
    {
        toolUseId = string.Empty;
        if (!content.Contains("<task-notification>", StringComparison.Ordinal))
        {
            return false;
        }

        const string OpenTag = "<tool-use-id>";
        const string CloseTag = "</tool-use-id>";
        var start = content.IndexOf(OpenTag, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        start += OpenTag.Length;
        var end = content.IndexOf(CloseTag, start, StringComparison.Ordinal);
        if (end <= start)
        {
            return false;
        }

        toolUseId = content[start..end];
        return true;
    }

    // Extracts the `usage` object off an assistant transcript line (AC-398). `messageId` (`message.id`) is
    // needed because the CLI can write more than one line for the same API response, each repeating the
    // identical usage figure — the caller must dedupe on this id before summing, or double-counts a call (AC-481).
    public static bool TryExtractUsage(string transcriptLine, out PluginTokenUsage? usage, out string? messageId)
    {
        usage = null;
        messageId = null;
        if (string.IsNullOrWhiteSpace(transcriptLine))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(transcriptLine);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeProperty)
                || typeProperty.GetString() != "assistant"
                || !root.TryGetProperty("message", out var message)
                || !message.TryGetProperty("usage", out var usageElement)
                || usageElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            messageId = message.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;

            usage = new PluginTokenUsage(
                _ReadTokenCount(usageElement, "input_tokens"),
                _ReadTokenCount(usageElement, "output_tokens"),
                _ReadTokenCount(usageElement, "cache_read_input_tokens"),
                _ReadTokenCount(usageElement, "cache_creation_input_tokens"));
            return true;
        }
    }

    // TryGetInt32 rather than GetInt32: a non-integral or out-of-range Number (unlikely, but not this parser's
    // contract to assume) must read as "none", not throw and kill the tail's async iterator.
    private static int _ReadTokenCount(JsonElement usage, string propertyName) =>
        usage.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var count)
            ? count
            : 0;
}
