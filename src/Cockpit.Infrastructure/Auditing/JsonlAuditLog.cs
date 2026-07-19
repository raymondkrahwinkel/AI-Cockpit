using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Auditing;

/// <summary>
/// The shared machinery behind the cockpit's audit trails (the consent trail #AC-47, the delegation trail #67):
/// an append-only, one-JSON-object-per-line file next to <c>cockpit.json</c> that survives a restart, can be
/// tailed while the app runs, and stays readable in a text editor. Append-only by contract — there is no write
/// path here that rewrites or truncates the file, so a record, once logged, cannot be erased by a later action,
/// which is the whole point of a trail a plugin (or an agent through it) cannot clear.
/// <para>
/// Extracted so the two trails share one implementation of the parts that must never drift (AC-59): the
/// never-throws append, the JSON-per-line parse that skips a half-written or hand-edited line rather than losing
/// the whole trail, and the surrogate-safe trim. A derived log supplies only what actually differs — its file
/// path, a human name for the warning, and how one entry is trimmed before it is written. The public
/// <see cref="RecordAsync"/>/<see cref="ReadRecentAsync"/> match the audit-log interfaces' shape, so a derived
/// class satisfies its interface simply by inheriting them.
/// </para>
/// </summary>
/// <typeparam name="T">The entry record. A reference type so a failed parse can be a null the reader filters out.</typeparam>
internal abstract class JsonlAuditLog<T>
    where T : class
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Read backward a block at a time; a trimmed JSON line is a few hundred bytes, so one block holds many.</summary>
    private const int ReadBlockSize = 16 * 1024;

    private readonly string _logFilePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    protected JsonlAuditLog(string logFilePath, ILogger logger)
    {
        _logFilePath = logFilePath;
        _logger = logger;
    }

    /// <summary>A short human name for this trail ("consent", "delegation"), used only in the warning when it cannot be read or written.</summary>
    protected abstract string LogName { get; }

    /// <summary>Returns the entry as it should be persisted — trimming the one free-text field the trail does not keep in full. The identity when nothing needs trimming.</summary>
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
            await File.AppendAllTextAsync(_logFilePath, line + Environment.NewLine, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Reads up to <paramref name="limit"/> parseable entries from the end of the file, newest first, without
    /// loading the whole log (C6): the append-only trail can grow to many MB on a long-lived install, so reading
    /// it whole and reversing it every call to keep the last N is the cost this avoids. Fixed blocks are read
    /// backward and split on <c>'\n'</c> — a byte that never occurs inside a multi-byte UTF-8 sequence, so a block
    /// boundary can never cut a character — and each complete line is decoded and parsed newest-first until enough
    /// valid entries are in hand or the file is exhausted. A blank or corrupt line is skipped and does not count
    /// toward the limit, matching the previous whole-file read.
    /// </summary>
    private async Task<IReadOnlyList<T>> _ReadRecentValidAsync(int limit, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            _logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ReadBlockSize, useAsync: true);

        var results = new List<T>(Math.Min(limit, 1024));
        var position = stream.Length;

        // The bytes of a line whose left boundary (an earlier '\n', or the start of the file) has not been reached
        // yet — they sit to the right of the block currently being read, so they are carried onto the next, earlier
        // block. A trimmed JSON line is tiny, so this stays a few hundred bytes even though a pathological long line
        // would grow it to that line's length.
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

    /// <summary>
    /// Trims <paramref name="text"/> to <paramref name="maxLength"/> characters plus an ellipsis — the trail is for
    /// recognising an action later, not for keeping a full copy of it. Surrogate-safe (C5): an astral character (an
    /// emoji in a command, say) straddling the limit is not cut through, which would otherwise leave a lone
    /// surrogate that is persisted as U+FFFD.
    /// </summary>
    protected static string TrimText(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
        return text[..cut] + "…";
    }
}
