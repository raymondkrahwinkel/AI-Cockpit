using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Sessions;

// Appends `SessionStateRecord`s to `session-state.jsonl` next to `cockpit.json` (AC-409). Shares the
// append/read idiom of `JsonlAuditLog{T}` but does not derive from it: that base assumes a trail that is
// never erased, while `CompactAsync` rewrites this file on purpose, and needs "last per pane", not "last N".
internal sealed class SessionStateStore : ISessionStateStore, ISingletonService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;
    private readonly ILogger<SessionStateStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SessionStateStore(ILogger<SessionStateStore> logger)
        : this(Path.Combine(CockpitConfigPath.Root, "session-state.jsonl"), logger)
    {
    }

    // Test seam: point the store at an arbitrary file.
    internal SessionStateStore(string filePath, ILogger<SessionStateStore> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public async Task RecordAsync(SessionStateRecord record, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = JsonSerializer.Serialize(record, SerializerOptions);
            await _AppendPrivateAsync(line + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A pane that could not be recorded must not fail the action that changed it (a session starting, a
            // live permission-mode switch) — losing the record is bad, blocking the session over it is worse.
            _logger.LogWarning(ex, "Could not append to the session-state log at {Path}.", _filePath);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<SessionStateRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            return [.. (await _ReadLatestPerPaneAsync(cancellationToken).ConfigureAwait(false)).Values];
        }
        catch (Exception ex)
        {
            // Derived state, not config: an unreadable file must not stop the cockpit from starting (contrast
            // CockpitConfigFileAccess, which refuses to on a corrupt cockpit.json).
            _logger.LogWarning(ex, "Could not read the session-state log at {Path}.", _filePath);
            return [];
        }
    }

    public async Task CompactAsync(IReadOnlySet<string>? knownPaneIds = null, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var latest = await _TryReadLatestPerPaneAsync(cancellationToken).ConfigureAwait(false);
            if (latest is null)
            {
                // A file that cannot be read, or read to nothing, is not one to rewrite: turning that into "empty"
                // here would throw away every record compaction was supposed to only fold, not lose. The warning
                // was already logged by _TryReadLatestPerPaneAsync.
                return;
            }

            // No roster means "fold, drop nothing" — a caller that cannot say which panes still exist must not have
            // its silence read as "none of them do".
            var kept = knownPaneIds is null
                ? latest.Values
                : latest.Values.Where(record => knownPaneIds.Contains(record.PaneId));
            var content = string.Concat(kept.Select(record => JsonSerializer.Serialize(record, SerializerOptions) + Environment.NewLine));

            // The existing atomic replace (sidecar + rename), not a bespoke one: a crash mid-compaction must leave
            // either the old file or the new one, never a half-written rewrite.
            CockpitConfigPath.ReplaceAtomicallyPrivate(_filePath, content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not compact the session-state log at {Path}.", _filePath);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<SessionStateRecord>?> TryLoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        var latest = await _TryReadLatestPerPaneAsync(cancellationToken).ConfigureAwait(false);
        return latest is null ? null : [.. latest.Values];
    }

    // Shared by `CompactAsync` and `TryLoadAsync`: a read failure and "bytes but nothing parsed" both come
    // back as `null`, not an empty dictionary — neither caller may treat "could not tell" as "nothing here".
    // `LoadAsync` does not use this: it collapses both into empty, having no write to protect.
    private async Task<IReadOnlyDictionary<string, SessionStateRecord>?> _TryReadLatestPerPaneAsync(CancellationToken cancellationToken)
    {
        try
        {
            var latest = await _ReadLatestPerPaneAsync(cancellationToken).ConfigureAwait(false);

            // The per-line parse never throws, so a file this build cannot understand at all arrives here as an
            // empty set rather than an error — same as a read failure, and gets the same answer. The length check
            // sits inside this try because FileInfo.Length can throw if the file vanished since File.Exists checked.
            if (latest.Count == 0 && new FileInfo(_filePath).Length > 0)
            {
                _logger.LogWarning(
                    "Could not make sense of any line in the session-state log at {Path}: it holds data but nothing in it could be parsed.",
                    _filePath);
                return null;
            }

            return latest;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the session-state log at {Path}.", _filePath);
            return null;
        }
    }

    // See `Auditing.JsonlAuditLog{T}._AppendPrivateAsync` — the same append-only-file idiom, kept here rather than shared because that base's other half (the tail reader) does not fit this store.
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

        await using var stream = new FileStream(_filePath, options);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken).ConfigureAwait(false);
    }

    // Reads every parseable record forward, keeping the last one seen per pane — a later line overwrites an
    // earlier one, i.e. "last record per pane wins". A blank or half-written line (crash mid-append, hand
    // edit) fails to parse and is skipped rather than losing every record before it.
    private async Task<Dictionary<string, SessionStateRecord>> _ReadLatestPerPaneAsync(CancellationToken cancellationToken)
    {
        var latest = new Dictionary<string, SessionStateRecord>(StringComparer.Ordinal);

        await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (_TryParse(line) is { } record)
            {
                latest[record.PaneId] = record;
            }
        }

        return latest;
    }

    // A line that parses but has no PaneId is still skipped: the serializer passes null for an omitted field
    // rather than refusing the line, so a hand-edited or truncated record could reach the caller keyless and
    // throw when used as a dictionary key — turning one bad line into "no session state at all".
    private static SessionStateRecord? _TryParse(string line)
    {
        try
        {
            return string.IsNullOrWhiteSpace(line)
                ? null
                : JsonSerializer.Deserialize<SessionStateRecord>(line, SerializerOptions) is { PaneId.Length: > 0 } record
                    ? record
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
