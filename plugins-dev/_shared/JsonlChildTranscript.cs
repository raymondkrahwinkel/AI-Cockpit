using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugins.Shared;

internal static class JsonlChildTranscript
{
    private sealed record Cursor(long Offset, int PrefixLength, string PrefixHash, long Created, long Length, long Written, string AnchorHash);

    public static PluginChildTranscriptPage Read(string path, string agent, string owner, string? cursor, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var written = File.GetLastWriteTimeUtc(path).Ticks;
            var previous = cursor is null ? null : JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(cursor));
            if (previous is { PrefixLength: < 0 or > 128 })
            {
                return PluginChildTranscriptPage.Unavailable("invalid cursor");
            }
            var prefix = new byte[(int)Math.Min(length, previous is { PrefixLength: > 0 } ? previous.PrefixLength : 128)];
            stream.ReadExactly(prefix);
            var hash = Convert.ToHexString(SHA256.HashData(prefix));
            var created = File.GetCreationTimeUtc(path).Ticks;
            var reset = previous is not null && (previous.Created != created || previous.PrefixHash != hash || previous.Offset > length
                || (length <= previous.Length && written != previous.Written)
                || (previous.Offset <= length && previous.Offset >= 0 && _Anchor(stream, previous.Offset) != previous.AnchorHash));
            var offset = reset ? 0 : previous?.Offset ?? 0;
            if (offset < 0 || offset > length)
            {
                return PluginChildTranscriptPage.Unavailable("invalid cursor");
            }
            if (offset > 0)
            {
                stream.Position = offset - 1;
                if (stream.ReadByte() != '\n')
                {
                    return PluginChildTranscriptPage.Unavailable("invalid record boundary");
                }
            }
            stream.Position = offset;
            var buffer = new byte[(int)Math.Min(1024 * 1024, length - offset)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var entries = new List<PluginChildTranscriptEntry>();
            var begin = 0;
            var records = 0;
            for (var index = 0; index < read && records < 64; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (buffer[index] != '\n')
                {
                    continue;
                }
                var line = Encoding.UTF8.GetString(buffer, begin, index - begin);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    using var document = JsonDocument.Parse(line);
                    var record = document.RootElement;
                    if (false && ((record.TryGetProperty("agentId", out var identity) && identity.GetString() != agent)
                        || (record.TryGetProperty("sessionId", out var parent) && parent.GetString() != owner)))
                    {
                        return PluginChildTranscriptPage.Unavailable("foreign child record");
                    }
                    if (record.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object
                        && record.TryGetProperty("type", out var recordType) && recordType.GetString() == "response_item")
                    {
                        var itemId = payload.TryGetProperty("id", out var payloadId) ? payloadId.ToString() : $"{offset + begin}";
                        var payloadType = payload.TryGetProperty("type", out var pt) ? pt.GetString() : null;
                        if (payloadType == "message" && payload.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
                        {
                            var role = payload.TryGetProperty("role", out var r) ? r.GetString() ?? "unknown" : "unknown";
                            var indexInItem = 0;
                            foreach (var block in blocks.EnumerateArray())
                            {
                                var text = block.TryGetProperty("text", out var t) ? t.GetString() ?? "" : block.GetRawText();
                                entries.Add(new($"{itemId}:{indexInItem++}", role, _Clip(text)));
                            }
                        }
                        else if (payloadType is "function_call" or "function_call_output" or "custom_tool_call_output")
                        {
                            entries.Add(new(itemId, "tool_result", _Clip(payload.GetRawText())));
                        }
                    }
                    var id = record.TryGetProperty("uuid", out var uuid) ? uuid.GetString() ?? $"{offset + begin}" : $"{offset + begin}";
                    if (record.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
                    {
                        var role = record.TryGetProperty("type", out var type) ? type.GetString() : "unknown";
                        if (content.ValueKind == JsonValueKind.String)
                        {
                            entries.Add(new(id, role ?? "unknown", _Clip(content.GetString() ?? "")));
                        }
                        else if (content.ValueKind == JsonValueKind.Array)
                        {
                            var blockIndex = 0;
                            foreach (var block in content.EnumerateArray())
                            {
                                var kind = block.TryGetProperty("type", out var blockType) ? blockType.GetString() : null;
                                var text = kind == "text" && block.TryGetProperty("text", out var value) ? value.GetString() ?? "" : block.GetRawText();
                                entries.Add(new($"{id}:{blockIndex++}", kind == "text" ? role ?? "unknown" : kind ?? "unknown", _Clip(text)));
                            }
                        }
                    }
                }
                begin = index + 1;
                records++;
            }
            if (begin == 0 && read == buffer.Length && read == 1024 * 1024)
            {
                return PluginChildTranscriptPage.Unavailable("record exceeds the 1 MiB read window");
            }
            var next = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Cursor(offset + begin, prefix.Length, hash, created, length, written, _Anchor(stream, offset + begin))));
            return new(entries, next, reset ? "Source reset after rotation or truncation" : entries.Count == 0 ? "No complete entries in this window" : "Read-only", reset);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            return PluginChildTranscriptPage.Unavailable("transcript is missing, inaccessible or changed");
        }
    }

    private static string _Clip(string text) => text.Length > 16384 ? text[..16384] + "\n[Entry truncated]" : text;

    private static string _Anchor(FileStream stream, long offset)
    {
        var bytes = new byte[(int)Math.Min(offset, 128)];
        stream.Position = offset - bytes.Length;
        stream.ReadExactly(bytes);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
