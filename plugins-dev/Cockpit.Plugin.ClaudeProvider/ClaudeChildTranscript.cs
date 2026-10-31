using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

internal static class ClaudeChildTranscript
{
    private sealed record Cursor(long Offset, int PrefixLength, string PrefixHash, long Created, long Length, long Written, string AnchorHash);

    public static PluginChildTranscriptPage Read(string stateDirectory, string workingDirectory, string owner, string agent, string? cursor, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(owner, "^[a-zA-Z0-9-]+$") || !Regex.IsMatch(agent, "^[a-zA-Z0-9-]+$"))
        {
            return PluginChildTranscriptPage.Unavailable("invalid source identity");
        }
        try
        {
            var root = Path.GetFullPath(stateDirectory);
            var project = Regex.Replace(Path.GetFullPath(workingDirectory), "[^a-zA-Z0-9]", "-");
            var path = Path.Combine(root, "projects", project, owner, "subagents", $"agent-{agent}.jsonl");
            for (var entry = new FileInfo(path) as FileSystemInfo; entry is not null; entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return PluginChildTranscriptPage.Unavailable("linked transcript source");
                }
                if (entry.FullName == root)
                {
                    break;
                }
            }
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
                    if (record.TryGetProperty("agentId", out var identity) && identity.GetString() != agent)
                    {
                        return PluginChildTranscriptPage.Unavailable("foreign child record");
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
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
