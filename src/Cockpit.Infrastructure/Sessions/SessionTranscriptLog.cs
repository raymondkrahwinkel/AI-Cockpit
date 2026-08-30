using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Sessions;

// Cockpit's own copy of every pane's conversation (AC-1090), one append-only `transcripts/<pane>.jsonl` next to
// `cockpit.json`. Replaces AC-684's `AssistantTranscriptFile`, which rewrote the whole file per row: a day's
// assistant conversation stored 3.3 MB and wrote 2.0 GB. Appending only the row that changed takes that to 1x.

// Same append/read shape as `SessionStateStore`, one level down: it keeps the last record per pane, this keeps
// the last version per row. AC-1151's debounce now coalesces *which rows* changed in a window, which is what
// keeps a streaming row from costing a line per delta.
internal sealed class SessionTranscriptLog : ISessionTranscriptStore, ISingletonService, IAsyncDisposable
{
    // AC-947: enough to survive a crash-loop (each recovery route archives once) without the folder filling up.
    private const int MaxArchives = 3;

    // AC-1151: measured against the live cockpit at 981 kB / four writes a minute (AC-1142). Five seconds
    // coalesces a turn's burst of rows into one write; worst case on a crash is losing what changed in that window.
    private static readonly TimeSpan DefaultDebounceWindow = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },

        // A row carries eight optional members and almost never fills them; writing those as nulls would put the
        // bytes this change exists to save straight back into every line.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _root;
    private readonly ILogger<SessionTranscriptLog> _logger;
    private readonly TimeSpan _debounceWindow;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // AC-1151 debounce state, guarded by `_pendingGate`: the rows waiting to be written, the write counting down
    // to them (if any), and the early-flush signal. One `TaskCompletionSource` per cycle, not a reusable semaphore
    // — a stray `Release()` can outlive its cycle and let the next one skip its window for free; a superseded TCS cannot.
    private readonly object _pendingGate = new();

    // Ordered so rows reach the file in the order they first appeared, with a lookup that lets a second version of
    // a row inside the same window replace the first in place rather than queue behind it.
    private readonly List<(string PaneId, TranscriptSnapshotEntry Entry)> _pending = [];
    private readonly Dictionary<(string PaneId, string RowId), int> _pendingIndex = [];
    private TaskCompletionSource? _flushSignal;
    private Task? _pendingFlush;

    public SessionTranscriptLog(ILogger<SessionTranscriptLog> logger)
        : this(CockpitConfigPath.TranscriptsRoot, logger, DefaultDebounceWindow)
    {
    }

    // Test seam: point the store at an arbitrary folder.
    internal SessionTranscriptLog(string root, ILogger<SessionTranscriptLog> logger)
        : this(root, logger, DefaultDebounceWindow)
    {
    }

    // Test seam: a debounce window short enough that a test does not have to wait out the real one.
    internal SessionTranscriptLog(string root, ILogger<SessionTranscriptLog> logger, TimeSpan debounceWindow)
    {
        _root = root;
        _logger = logger;
        _debounceWindow = debounceWindow;
    }

    // How many times this instance has actually appended to disk. Observed by the debounce test; not production code.
    internal int WriteCountForTests { get; private set; }

    // How many bytes this instance has handed to the file system. The measurement AC-1090 turns on: divided by the
    // log's final size it is the write amplification this store exists to bring back to ~1x.
    internal long BytesWrittenForTests { get; private set; }

    internal string LogPath(string paneId) => Path.Combine(_root, _FileName(paneId));

    public Task AppendAsync(string paneId, TranscriptSnapshotEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_pendingGate)
        {
            var key = (paneId, entry.Id);
            if (_pendingIndex.TryGetValue(key, out var index))
            {
                _pending[index] = (paneId, entry);
            }
            else
            {
                _pendingIndex[key] = _pending.Count;
                _pending.Add((paneId, entry));
            }

            // Invariant: `_flushSignal` is non-null whenever `_pendingFlush` is — set here, in the same lock, so
            // `FlushAsync` can never observe one without the other and silently wait out the window instead.
            if (_pendingFlush is null)
            {
                _flushSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingFlush = _DebounceThenWriteAsync(_flushSignal, cancellationToken);
            }

            return _pendingFlush;
        }
    }

    public async Task<IReadOnlyList<TranscriptSnapshotEntry>> LoadAsync(string paneId, CancellationToken cancellationToken = default)
    {
        var path = LogPath(paneId);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            // Last version per row, in the order the rows first appeared. A blank or half-written line (crash
            // mid-append, hand edit) is skipped rather than losing every row before it — `SessionStateStore`'s
            // contract for a line it cannot parse, and AC-684's for a row this build cannot make sense of.
            var order = new List<string>();
            var latest = new Dictionary<string, TranscriptSnapshotEntry>(StringComparer.Ordinal);

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, useAsync: true);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (_TryParse(line) is not { } entry)
                {
                    continue;
                }

                if (!latest.ContainsKey(entry.Id))
                {
                    order.Add(entry.Id);
                }

                latest[entry.Id] = entry;
            }

            return [.. order.Select(id => latest[id])];
        }
        catch (Exception ex)
        {
            // Derived state, same contract as SessionStateStore.LoadAsync: a log this build cannot read must not
            // stop the pane from starting — it only starts without its history.
            _logger.LogWarning(ex, "Could not read the transcript log at {Path}.", path);
            return [];
        }
    }

    public async Task ArchiveAsync(string paneId, CancellationToken cancellationToken = default)
    {
        // AC-1151: rows not yet on disk must land before the file is moved, or the archive silently drops however
        // many were still waiting out the window.
        await FlushAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = LogPath(paneId);
            if (!File.Exists(path))
            {
                // A second archive-worthy start with no rows recorded in between must not overwrite the real
                // archive with an empty one.
                return;
            }

            var stem = Path.GetFileNameWithoutExtension(path);
            var archivePath = Path.Combine(_root, $"{stem}.previous-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.jsonl");

            // A rename, not a copy-then-delete: the file keeps the owner-only mode it was created with.
            File.Move(path, archivePath, overwrite: true);

            var stale = Directory.EnumerateFiles(_root, $"{stem}.previous-*.jsonl")
                .OrderByDescending(existing => existing, StringComparer.Ordinal)
                .Skip(MaxArchives);
            foreach (var existing in stale)
            {
                File.Delete(existing);
            }
        }
        catch (Exception ex)
        {
            // Same contract as AppendAsync: an archive that could not be made must not stop the session from starting.
            _logger.LogWarning(ex, "Could not archive the transcript log for pane {PaneId}.", paneId);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // AC-1151: forces pending rows out now instead of waiting for the debounce window — used before a log is
    // archived, and on shutdown, so neither ever sees a write still counting down. A no-op when nothing is pending.
    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        Task? pending;
        TaskCompletionSource? signal;
        lock (_pendingGate)
        {
            pending = _pendingFlush;
            signal = _flushSignal;
        }

        if (pending is not null)
        {
            // The cycle's own task completes only once its write has landed, so awaiting it is enough.
            signal?.TrySetResult();
            await pending.ConfigureAwait(false);
            return;
        }

        // Nothing counting down — but a cycle may have taken its rows already and still be writing them. Waiting
        // for the write lock to fall free is what makes "flushed" mean on disk rather than merely scheduled.
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        _writeLock.Release();
    }

    // AC-1202's container-dispose budget (800 ms, `Program.DisposeServiceContainerAsync`) already bounds this on
    // the way out — no separate exit-watchdog wiring needed for the final write.
    public ValueTask DisposeAsync() => new(FlushAsync(CancellationToken.None));

    private async Task _DebounceThenWriteAsync(TaskCompletionSource flushSignal, CancellationToken cancellationToken)
    {
        // A real suspension point before touching `_pendingGate` again — without it, a debounce window short
        // enough to finish synchronously (a test's `TimeSpan.Zero`) would let this method's own reset of
        // `_pendingFlush` run before `AppendAsync` has assigned it, and the assignment would clobber it back.
        await Task.Yield();

        using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            try
            {
                await Task.WhenAny(Task.Delay(_debounceWindow, delayCts.Token), flushSignal.Task).ConfigureAwait(false);
            }
            finally
            {
                // Stops the delay timer early when the flush signal won; a no-op when the delay already fired.
                delayCts.Cancel();
            }
        }

        // The write lock is taken *before* the pending state is handed over, so the moment where this cycle no
        // longer advertises itself and its rows are not yet on disk lies entirely inside the lock — otherwise
        // `FlushAsync` finds nothing to wait for and returns while those rows are still in flight.
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<(string PaneId, TranscriptSnapshotEntry Entry)> batch;
            lock (_pendingGate)
            {
                batch = [.. _pending];
                _pending.Clear();
                _pendingIndex.Clear();
                _pendingFlush = null;
                _flushSignal = null;
            }

            CockpitConfigPath.EnsurePrivateDirectory(_root);
            foreach (var group in batch.GroupBy(row => row.PaneId, StringComparer.Ordinal))
            {
                var content = string.Concat(group.Select(row =>
                    JsonSerializer.Serialize(row.Entry, SerializerOptions) + Environment.NewLine));
                await _AppendPrivateAsync(LogPath(group.Key), content, cancellationToken).ConfigureAwait(false);
                WriteCountForTests++;
                BytesWrittenForTests += Encoding.UTF8.GetByteCount(content);
            }
        }
        catch (Exception ex)
        {
            // A transcript that could not be recorded must not fail the turn that changed it — losing rows is bad,
            // blocking the conversation over it is worse (same contract as SessionStateStore.RecordAsync).
            _logger.LogWarning(ex, "Could not append to the transcript logs under {Root}.", _root);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // See `SessionStateStore._AppendPrivateAsync` — the same append-only-file idiom, and the same reason it is
    // repeated rather than shared: `JsonlAuditLog{T}`'s other half (the tail reader) does not fit this store either.
    private static async Task _AppendPrivateAsync(string path, string content, CancellationToken cancellationToken)
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

        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content), cancellationToken).ConfigureAwait(false);
    }

    // A pane id becomes a file name here, so it is spelled out rather than trusted: today's ids are a GUID or the
    // assistant's reserved literal, and neither this store nor its callers are the place to learn that changed.
    private static string _FileName(string paneId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string([.. paneId.Select(character => invalid.Contains(character) ? '_' : character)]);
        return (safe.Length == 0 ? "unnamed" : safe) + ".jsonl";
    }

    // A line that parses but has no id is skipped for the same reason `SessionStateStore._TryParse` skips a
    // keyless record: the serializer passes null for an omitted field rather than refusing the line.
    private static TranscriptSnapshotEntry? _TryParse(string line)
    {
        try
        {
            return string.IsNullOrWhiteSpace(line)
                ? null
                : JsonSerializer.Deserialize<TranscriptSnapshotEntry>(line, SerializerOptions) is { Id.Length: > 0 } entry
                    ? entry
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
