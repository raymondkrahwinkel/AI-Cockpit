using System.Text;
using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// Parses one line of a <c>claude</c> session's live JSONL transcript
/// (<c>&lt;config-dir&gt;/projects/&lt;cwd-hash&gt;/&lt;session-id&gt;.jsonl</c>), extracting the assistant's
/// spoken-worthy text for the host's TTY read-aloud (#35b). Only <c>{"type":"assistant"}</c> lines carry
/// anything to say; <c>tool_use</c>/<c>thinking</c> content blocks are skipped, and <c>user</c>/<c>system</c>
/// lines are ignored entirely — this is a transcript reader, not a TUI/ANSI parser. The plugin owns this format
/// knowledge (weg A: it cannot reference the core, and the core must know nothing of Claude's JSONL shape).
/// </summary>
internal static class ClaudeTranscriptLineParser
{
    /// <summary>
    /// Extracts and concatenates every <c>content[].type == "text"</c> block from an assistant transcript
    /// line. Returns false (with an empty <paramref name="text"/>) for non-assistant lines, lines with no
    /// text content (pure tool-use turns), a blank line, or a line that fails to parse as JSON — the last
    /// case covers a tail read landing mid-write, which is a transient artefact, not an error to surface.
    /// </summary>
    public static bool TryExtractAssistantText(string transcriptLine, out string text)
    {
        text = string.Empty;
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
                || !message.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var builder = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var blockType)
                    && blockType.GetString() == "text"
                    && block.TryGetProperty("text", out var blockText))
                {
                    builder.Append(blockText.GetString());
                }
            }

            if (builder.Length == 0)
            {
                return false;
            }

            text = builder.ToString();
            return true;
        }
    }

    /// <summary>
    /// Extracts the <c>usage</c> object off an assistant transcript line (AC-398) — the same token buckets
    /// <c>ClaudeStreamJson</c> reads off the SDK path's <c>result</c> event. <paramref name="messageId"/> is the
    /// API response (<c>message.id</c>) this usage belongs to: the CLI can write more than one transcript line for
    /// the same response (progressive content-block saves within one turn), and every one of those lines repeats
    /// the identical usage figure — the caller must dedupe on this id before summing, or it double- (sometimes
    /// 2-3x-) counts a single API call, the same class of bug as AC-481's cumulative-cost mis-sum. Returns false
    /// (with a null <paramref name="usage"/> and <paramref name="messageId"/>) for a non-assistant line, one with
    /// no <c>usage</c> object, a blank line, or a line that fails to parse as JSON (a tail read landing mid-write).
    /// </summary>
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
