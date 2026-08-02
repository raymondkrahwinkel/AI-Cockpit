using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

// Parses one line of a `claude` session's live JSONL transcript
// (`&lt;config-dir&gt;/projects/&lt;cwd-hash&gt;/&lt;session-id&gt;.jsonl`), extracting the turn-activity,
// usage and background-work signals the host's TTY status dot (#39) needs. The plugin owns this format
// knowledge (weg A: it cannot reference the core, and the core must know nothing of Claude's JSONL shape).
internal static class ClaudeTranscriptLineParser
{
    // Reads the CLI's own count of sub-agents still running, which it writes on the
    // `{"type":"system","subtype":"turn_duration"}` line that closes every turn (AC-276). The field is only
    // present when something is pending, so its absence is the count zero — measured across 232 transcripts:
    // 677 of 2475 turn_duration lines carry it, with values 1..19 and never 0.
    //
    // This is a count the provider states, not one this reader keeps: every turn restates it, so a line missed
    // mid-write costs one stale reading rather than desynchronising a ledger. It counts *sub-agents only* —
    // measured on 608 turn endings that had a shell but no agent open, 594 carried no field at all — so shells are
    // tracked separately by `TryReadBackgroundShellTransition`.
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

    // Reads a backgrounded shell starting or ending (AC-276), keyed on the `tool_use` id that both ends of
    // the exchange carry. A start is a `Bash` tool call with `run_in_background: true`; an end is the
    // `{"type":"queue-operation"}` line whose content holds the CLI's `&lt;task-notification&gt;` block,
    // naming the same `&lt;tool-use-id&gt;`.
    //
    // Unlike the sub-agent count above there is no provider-stated total for shells, so this one *is* a
    // ledger and carries a ledger's risk: a missed end leaves a shell counted forever. That is deliberate and
    // bounded — an outstanding shell only withholds the "session finished" notification, never the status, so the
    // worst case is a missing notification rather than a session stuck on "working".
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

    // Extracts the `usage` object off an assistant transcript line (AC-398) — the same token buckets
    // `ClaudeStreamJson` reads off the SDK path's `result` event. `messageId` is the
    // API response (`message.id`) this usage belongs to: the CLI can write more than one transcript line for
    // the same response (progressive content-block saves within one turn), and every one of those lines repeats
    // the identical usage figure — the caller must dedupe on this id before summing, or it double- (sometimes
    // 2-3x-) counts a single API call, the same class of bug as AC-481's cumulative-cost mis-sum. Returns false
    // (with a null `usage` and `messageId`) for a non-assistant line, one with
    // no `usage` object, a blank line, or a line that fails to parse as JSON (a tail read landing mid-write).
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
