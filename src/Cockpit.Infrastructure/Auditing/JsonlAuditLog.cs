using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Cockpit.Infrastructure.Formatting;

namespace Cockpit.Infrastructure.Auditing;

// Shared machinery behind the cockpit's audit trails (consent #AC-47, delegation #67): an append-only,
// one-JSON-object-per-line file next to `cockpit.json`. No write path here rewrites or truncates, so a
// record, once logged, cannot be erased (AC-59). `T`: reference type so a failed parse can be null.
internal abstract class JsonlAuditLog<T>
    where T : class
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // Read backward a block at a time; a trimmed JSON line is a few hundred bytes, so one block holds many.
    private const int ReadBlockSize = 16 * 1024;

    private readonly string _logFilePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    protected JsonlAuditLog(string logFilePath, ILogger logger)
    {
        _logFilePath = logFilePath;
        _logger = logger;
    }

    // The file this trail appends to. Exposed so the startup repair's guard test can ask each trail where it
    // actually writes, rather than trusting that the list it walks (`AuditTrailFiles`) still names the
    // same four files the trails do — a list that has quietly drifted repairs nothing and reads as if it does.
    internal string FilePath => _logFilePath;

    // A short human name for this trail ("consent", "delegation"), used only in the warning when it cannot be read or written.
    protected abstract string LogName { get; }

    // Returns the entry as it should be persisted — trimming the one free-text field the trail does not keep in full. The identity when nothing needs trimming.
    protected abstract T PrepareForWrite(T entry);

    public async Task RecordAsync(T entry, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = JsonSerializer.Serialize(PrepareForWrite(entry), SerializerOptions);
            await _AppendPrivateAsync(line + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A broken audit log must not take the action down with it — losing the record is bad, blocking the
            // operator's approved action (or a delegation) is worse. Logged rather than swallowed, so a silently
            // unwritable log still surfaces.
            _logger.LogWarning(ex, "Could not append to the {LogName} audit log at {Path}.", LogName, _logFilePath);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<T>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || !File.Exists(_logFilePath))
        {
            return [];
        }

        try
        {
            return await _ReadRecentValidAsync(limit, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the {LogName} audit log at {Path}.", LogName, _logFilePath);
            return [];
        }
    }

    // Appends `line`, creating the file owner-only if it is not there yet — a trail holds free text
    // (a command, a prompt, a message) that may contain a token or path, and the default umask is
    // world-readable. `FileShare.Read` turns away a concurrent writer with a logged `IOException`.
    private async Task _AppendPrivateAsync(string line, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.Read,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using var stream = new FileStream(_logFilePath, options);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken).ConfigureAwait(false);
    }

    // Reads up to `limit` parseable entries from the end of the file, newest first, without loading the
    // whole log (C6). Fixed blocks are read backward and split on `'\n'`, a byte that never occurs inside
    // a multi-byte UTF-8 sequence, so a block boundary can never cut a character. A blank or corrupt line is skipped.
    private async Task<IReadOnlyList<T>> _ReadRecentValidAsync(int limit, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            _logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ReadBlockSize, useAsync: true);

        var results = new List<T>(Math.Min(limit, 1024));
        var position = stream.Length;

        // Bytes of a line whose left boundary (an earlier '\n', or file start) has not been reached yet
        // are carried onto the next, earlier block. Stays a few hundred bytes for a normal trimmed line.
        var carry = Array.Empty<byte>();

        while (position > 0 && results.Count < limit)
        {
            var toRead = (int)Math.Min(ReadBlockSize, position);
            position -= toRead;

            var buffer = new byte[toRead + carry.Length];
            stream.Position = position;
            await stream.ReadExactlyAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
            Buffer.BlockCopy(carry, 0, buffer, toRead, carry.Length);

            // Walk right-to-left, emitting each line that sits to the right of a '\n' (so newest first).
            var segmentEnd = buffer.Length;
            for (var i = buffer.Length - 1; i >= 0 && results.Count < limit; i--)
            {
                if (buffer[i] == (byte)'\n')
                {
                    _EmitLine(buffer, i + 1, segmentEnd, results, limit);
                    segmentEnd = i;
                }
            }

            if (position == 0)
            {
                // buffer[0..segmentEnd) is bounded on the left by the start of the file, so it is a complete line.
                _EmitLine(buffer, 0, segmentEnd, results, limit);
            }
            else
            {
                // Its left boundary is in an earlier block; carry it so the next read completes it.
                carry = buffer[..segmentEnd];
            }
        }

        return results;
    }

    private void _EmitLine(byte[] buffer, int start, int end, List<T> results, int limit)
    {
        if (results.Count >= limit || end <= start)
        {
            return;
        }

        // Drop the '\r' of a "\r\n" terminator (the file is written with Environment.NewLine); a raw CR never
        // occurs inside a JSON object, so nothing else is touched.
        if (buffer[end - 1] == (byte)'\r')
        {
            end--;
        }

        if (end <= start)
        {
            return;
        }

        var line = Encoding.UTF8.GetString(buffer, start, end - start);
        if (_TryParse(line) is { } entry)
        {
            results.Add(entry);
        }
    }

    // A half-written or hand-edited line is skipped rather than throwing away the whole trail.
    private static T? _TryParse(string line)
    {
        try
        {
            return string.IsNullOrWhiteSpace(line)
                ? null
                : JsonSerializer.Deserialize<T>(line, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Trims `text` to `maxLength` characters plus an ellipsis — the trail is for recognising an action
    // later, not keeping a full copy. The surrogate-safe cut lives in `BoundedText`, shared with other
    // callers so the rule has one implementation.
    protected static string TrimText(string text, int maxLength) => BoundedText.Trim(text, maxLength);
}
