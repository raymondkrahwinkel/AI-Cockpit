using System.Text;
using System.Text.Json;

namespace Cockpit.Core.Voice;

/// <summary>
/// Parses one line of a <c>claude</c> session's live JSONL transcript
/// (<c>&lt;config-dir&gt;/projects/&lt;cwd-hash&gt;/&lt;session-id&gt;.jsonl</c>), extracting the assistant's
/// spoken-worthy text for TTY read-aloud (#35b). Only <c>{"type":"assistant"}</c> lines carry anything to
/// say; <c>tool_use</c>/<c>thinking</c> content blocks are skipped, and <c>user</c>/<c>system</c> lines are
/// ignored entirely — this is a transcript reader, not a TUI/ANSI parser.
/// </summary>
public static class ClaudeTranscriptLineParser
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
}
