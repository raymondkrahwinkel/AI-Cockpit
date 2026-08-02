using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Sessions;

// Appends `SessionStateRecord`s to `session-state.jsonl` next to `cockpit.json` (AC-409).
//
// Shares the append/read idiom of `JsonlAuditLog{T}` — `FileMode.Append` with
// `FileShare.Read`, owner-only creation, a write lock, and a never-throws-to-the-caller append — but
// does not derive from it: that base's whole point is that a trail, once written, cannot be erased, and
// `CompactAsync` rewrites this file on purpose. Session state is derived, not a trail; it is expected
// to shrink. The tail-reading block reader that base class uses to answer "the last N" is not reused either —
// this store needs "the last record per pane across the whole file" instead, and the file stays small through
// compaction, so a plain forward read is both correct and simpler.
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

    // The read `CompactAsync` and `TryLoadAsync` share: a read failure and "the file has
    // bytes but no line in it parsed" both come back as `null` rather than an empty dictionary —
    // neither caller may treat "could not tell" as "there is nothing here" (see each method's own doc for why).
    // `LoadAsync` does not use this: its contract is the opposite on purpose, collapsing both into
    // empty for a restore that has no write to protect and nothing better to fall back on.
    private async Task<IReadOnlyDictionary<string, SessionStateRecord>?> _TryReadLatestPerPaneAsync(CancellationToken cancellationToken)
    {
        try
        {
            var latest = await _ReadLatestPerPaneAsync(cancellationToken).ConfigureAwait(false);

            // The per-line parse never throws — a line it cannot make sense of is simply skipped — so a file this
            // build cannot understand at all (written by a newer one, re-encoded, hand-mangled) arrives here as an
            // empty set rather than as an error. Parsing nothing out of a file that has something in it is the same
            // situation as failing to read it, and gets the same answer. The length check sits inside this try
            // rather than after it because FileInfo.Length throws for a file that has gone since File.Exists said
            // otherwise: CompactAsync used to catch that in its own outer try, but TryLoadAsync has none, and its
            // contract is to answer null when it cannot tell — not to throw at a caller composing a write.
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

    // Reads every parseable record forward, keeping only the last one seen per pane — a later line for the same
    // pane overwrites an earlier one in the dictionary, which is exactly "last record per pane wins". A blank or
    // half-written line (a crash mid-append, or a hand edit) fails to parse and is skipped rather than losing
    // every record before it.
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

    // A line that does not parse into a record with a pane to key it on is skipped. The pane check is not
    // belt-and-braces: `SessionStateRecord.PaneId` is a positional parameter, and the serializer
    // passes null for one a line omits rather than refusing the line — so a hand-edited or truncated-but-still-valid
    // object would reach the caller with no key, and the dictionary it is being put into would throw on that null.
    // That throw escapes the whole read, which would turn one bad line into "there is no session state at all" —
    // the opposite of what skipping a bad line is for.
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
