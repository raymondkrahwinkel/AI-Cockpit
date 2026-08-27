using System.Text.Json;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Assistant;

// The assistant's transcript as a JSON snapshot next to `cockpit.json` (AC-684) — what `ISessionStateStore`
// leaves out, same overwrite-whole idiom as `AssistantMemoryFile.NoteCurrentStateAsync`. AC-1151: SaveAsync
// coalesces into at most one actual write per debounce window, plus one more via FlushAsync (archive, shutdown).
internal sealed class AssistantTranscriptFile : IAssistantTranscriptStore, ISingletonService, IAsyncDisposable
{
    // AC-947: enough to survive a crash-loop (each recovery route archives once) without the folder filling up.
    private const int MaxArchives = 3;

    // AC-1151: measured against the live cockpit at 981 kB / four writes a minute (AC-1142). Five seconds
    // coalesces a turn's burst of rows into one write; worst case on a crash is losing what changed in that window.
    private static readonly TimeSpan DefaultDebounceWindow = TimeSpan.FromSeconds(5);

    private readonly string _filePath;
    private readonly ILogger<AssistantTranscriptFile> _logger;
    private readonly TimeSpan _debounceWindow;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // AC-1151 debounce state, guarded by `_pendingGate`: the pending entries, the write counting down to them
    // (if any), and the early-flush signal. One `TaskCompletionSource` per cycle, not a reusable semaphore — a
    // stray `Release()` can outlive its cycle and let the next one skip its window for free; a superseded TCS cannot.
    private readonly object _pendingGate = new();
    private TaskCompletionSource? _flushSignal;
    private IReadOnlyList<AssistantTranscriptSnapshotEntry>? _pendingEntries;
    private Task? _pendingFlush;

    public AssistantTranscriptFile(ILogger<AssistantTranscriptFile> logger)
        : this(CockpitConfigPath.AssistantTranscript, logger, DefaultDebounceWindow)
    {
    }

    // Test seam: point the store at an arbitrary file.
    internal AssistantTranscriptFile(string filePath, ILogger<AssistantTranscriptFile> logger)
        : this(filePath, logger, DefaultDebounceWindow)
    {
    }

    // Test seam: a debounce window short enough that a test does not have to wait out the real one.
    internal AssistantTranscriptFile(string filePath, ILogger<AssistantTranscriptFile> logger, TimeSpan debounceWindow)
    {
        _filePath = filePath;
        _logger = logger;
        _debounceWindow = debounceWindow;
    }

    // AC-1151: how many times this instance has actually replaced the file on disk. Observed by the debounce
    // test; not used by production code.
    internal int WriteCountForTests { get; private set; }

    public async Task<IReadOnlyList<AssistantTranscriptSnapshotEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<IReadOnlyList<AssistantTranscriptSnapshotEntry>>(
                stream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex)
        {
            // Derived state, same contract as SessionStateStore.LoadAsync: a file this build cannot read must not
            // stop the assistant from starting — it only starts without its history.
            _logger.LogWarning(ex, "Could not read the assistant transcript at {Path}.", _filePath);
            return [];
        }
    }

    // AC-684/AC-1151: every new row schedules a save, but only the first call in a window starts a write — later
    // calls just replace the pending entries and share that write's task and token. Today's one caller always
    // passes CancellationToken.None (harmless); a future caller with a real token would have it silently dropped.
    public Task SaveAsync(IReadOnlyList<AssistantTranscriptSnapshotEntry> entries, CancellationToken cancellationToken = default)
    {
        lock (_pendingGate)
        {
            _pendingEntries = entries;

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

    // AC-1151: forces a pending write out now instead of waiting for the debounce window — used before the file
    // is archived or moved, and on shutdown, so neither ever sees a write still counting down. A no-op when
    // nothing is pending.
    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        Task? pending;
        TaskCompletionSource? signal;
        lock (_pendingGate)
        {
            pending = _pendingFlush;
            signal = _flushSignal;
        }

        if (pending is null)
        {
            return;
        }

        signal?.TrySetResult();
        await pending.ConfigureAwait(false);
    }

    private async Task _DebounceThenWriteAsync(TaskCompletionSource flushSignal, CancellationToken cancellationToken)
    {
        // A real suspension point before touching `_pendingGate` again — without it, a debounce window short
        // enough to finish synchronously (a test's `TimeSpan.Zero`) would let this method's own reset of
        // `_pendingFlush` run before `SaveAsync` has assigned it, and the assignment would clobber it back.
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

        IReadOnlyList<AssistantTranscriptSnapshotEntry> entries;
        lock (_pendingGate)
        {
            entries = _pendingEntries!;
            _pendingEntries = null;
            _pendingFlush = null;
            _flushSignal = null;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CockpitConfigPath.EnsurePrivateDirectory(Path.GetDirectoryName(_filePath) ?? CockpitConfigPath.Root);
            var content = JsonSerializer.Serialize(entries);

            // Atomic replace (sidecar + rename), the same idiom SessionStateStore.CompactAsync uses: a crash
            // mid-write must leave either the previous snapshot or the new one, never a half-written file the
            // next start cannot parse — the debounce above only changed *when* this runs, not this guarantee.
            CockpitConfigPath.ReplaceAtomicallyPrivate(_filePath, content);
            WriteCountForTests++;
        }
        catch (Exception ex)
        {
            // A transcript that could not be saved must not fail the turn that changed it — losing the snapshot
            // is bad, blocking the conversation over it is worse (same contract as SessionStateStore.RecordAsync).
            _logger.LogWarning(ex, "Could not save the assistant transcript at {Path}.", _filePath);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // AC-1151: the container-dispose budget (AC-1202, 800 ms, `Program.DisposeServiceContainerAsync`) already
    // bounds this on the way out — no separate exit-watchdog wiring needed for the final write.
    public ValueTask DisposeAsync() => new(FlushAsync(CancellationToken.None));

    public async Task ArchiveAsync(CancellationToken cancellationToken = default)
    {
        // AC-1151: a debounced write not yet on disk must land before the file is moved, or the archive
        // silently drops however many rows were still waiting out the window.
        await FlushAsync(cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                // A second archive-worthy start with no rows saved in between must not overwrite the real
                // archive with an empty one.
                return;
            }

            var directory = Path.GetDirectoryName(_filePath) ?? CockpitConfigPath.Root;
            var stem = Path.GetFileNameWithoutExtension(_filePath);
            var archivePath = Path.Combine(directory, $"{stem}.previous-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");

            // A rename, not a copy-then-delete: the file keeps the owner-only mode `ReplaceAtomicallyPrivate` gave it.
            File.Move(_filePath, archivePath, overwrite: true);

            var stale = Directory.EnumerateFiles(directory, $"{stem}.previous-*.json")
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .Skip(MaxArchives);
            foreach (var path in stale)
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            // Same contract as SaveAsync: an archive that could not be made must not stop the session from starting.
            _logger.LogWarning(ex, "Could not archive the assistant transcript at {Path}.", _filePath);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
