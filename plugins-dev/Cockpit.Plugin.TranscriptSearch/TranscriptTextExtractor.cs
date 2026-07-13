using System.Text;
using System.Text.Json;

namespace Cockpit.Plugin.TranscriptSearch;

/// <summary>The human-readable text of one transcript JSONL entry: who said it and what, for #9 transcript search.</summary>
public sealed record TranscriptEntryText(string Role, string Text);

/// <summary>
/// Pulls the searchable prose out of a single <c>claude</c> transcript JSONL line (#9): a user prompt or an
/// assistant reply. A line's <c>message.content</c> is either a plain string (a typed prompt) or an array of
/// blocks, of which only the <c>text</c> blocks are prose — thinking, tool-use and tool-result blocks are
/// skipped, so a tool-result "user" record yields nothing. Anything unparseable or without prose returns null,
/// so a malformed or non-message line simply isn't a search target rather than throwing.
/// </summary>
public static class TranscriptTextExtractor
{
    public static TranscriptEntryText? Extract(string? jsonLine)
    {
        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(jsonLine);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // The role lives on the top-level record type (user/assistant) or, failing that, message.role.
            var role = _StringOrNull(root, "type");
            if (role is not ("user" or "assistant"))
            {
                role = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object
                    ? _StringOrNull(m, "role")
                    : null;
            }

            if (role is not ("user" or "assistant"))
            {
                return null;
            }

            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!message.TryGetProperty("content", out var content))
            {
                return null;
            }

            var text = _ExtractContentText(content);
            return string.IsNullOrWhiteSpace(text) ? null : new TranscriptEntryText(role, text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? _ExtractContentText(JsonElement content)
    {
        // A plain-string content is a typed prompt; an array is a block list where only text blocks are prose.
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && _StringOrNull(block, "type") == "text"
                && _StringOrNull(block, "text") is { Length: > 0 } blockText)
            {
                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(blockText);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string? _StringOrNull(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
