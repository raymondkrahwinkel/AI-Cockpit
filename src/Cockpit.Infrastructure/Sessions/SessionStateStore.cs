using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// Appends <see cref="SessionStateRecord"/>s to <c>session-state.jsonl</c> next to <c>cockpit.json</c> (AC-409).
/// <para>
/// Shares the append/read idiom of <c>JsonlAuditLog{T}</c> — <see cref="FileMode.Append"/> with
/// <see cref="FileShare.Read"/>, owner-only creation, a write lock, and a never-throws-to-the-caller append — but
/// does not derive from it: that base's whole point is that a trail, once written, cannot be erased, and
/// <see cref="CompactAsync"/> rewrites this file on purpose. Session state is derived, not a trail; it is expected
/// to shrink. The tail-reading block reader that base class uses to answer "the last N" is not reused either —
/// this store needs "the last record per pane across the whole file" instead, and the file stays small through
/// compaction, so a plain forward read is both correct and simpler.
/// </para>
/// </summary>
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

    /// <summary>Test seam: point the store at an arbitrary file.</summary>
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

            IReadOnlyDictionary<string, SessionStateRecord> latest;
            try
            {
                latest = await _ReadLatestPerPaneAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A file that cannot be read is not one to rewrite: turning "unreadable" into "empty" here would
                // throw away every record compaction was supposed to only fold, not lose.
                _logger.LogWarning(ex, "Could not read the session-state log at {Path} for compaction; leaving it untouched.", _filePath);
                return;
            }

            // Parsing nothing out of a file that has something in it is the same situation as failing to read it,
            // and gets the same answer: leave it alone. The per-line parse never throws — a line it cannot make
            // sense of is simply skipped — so a file this build cannot understand at all (written by a newer one,
            // re-encoded, hand-mangled) arrives here as an empty set rather than as an error, and rewriting on
            // that would replace every record with nothing. The read-failure branch above already refuses to turn
            // "unreadable" into "empty"; this is the same refusal on the path that does not raise.
            if (latest.Count == 0 && new FileInfo(_filePath).Length > 0)
            {
                _logger.LogWarning(
                    "Not compacting the session-state log at {Path}: it holds data but no line in it could be read, so rewriting it would discard all of it.",
                    _filePath);
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

    /// <summary>See <see cref="Auditing.JsonlAuditLog{T}._AppendPrivateAsync"/> — the same append-only-file idiom, kept here rather than shared because that base's other half (the tail reader) does not fit this store.</summary>
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

    /// <summary>
    /// Reads every parseable record forward, keeping only the last one seen per pane — a later line for the same
    /// pane overwrites an earlier one in the dictionary, which is exactly "last record per pane wins". A blank or
    /// half-written line (a crash mid-append, or a hand edit) fails to parse and is skipped rather than losing
    /// every record before it.
    /// </summary>
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

    /// <summary>
    /// A line that does not parse into a record with a pane to key it on is skipped. The pane check is not
    /// belt-and-braces: <see cref="SessionStateRecord.PaneId"/> is a positional parameter, and the serializer
    /// passes null for one a line omits rather than refusing the line — so a hand-edited or truncated-but-still-valid
    /// object would reach the caller with no key, and the dictionary it is being put into would throw on that null.
    /// That throw escapes the whole read, which would turn one bad line into "there is no session state at all" —
    /// the opposite of what skipping a bad line is for.
    /// </summary>
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
