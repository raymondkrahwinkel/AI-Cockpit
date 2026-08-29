namespace Cockpit.Infrastructure.Configuration;

// AC-1108: folds every UpdateAsync raised in the scope into one read-modify-write (measured 60+ round-trips per
// Apply otherwise). Each mutation applies to a shared in-memory copy immediately; only the write is deferred, so
// the thirteen sequentially-awaited SaveXxxCommands can't deadlock waiting on each other to flush first.
public sealed class CockpitConfigWriteBatch : IAsyncDisposable
{
    private static readonly AsyncLocal<CockpitConfigWriteBatch?> _current = new();

    private readonly CockpitConfigWriteBatch? _previous;
    private readonly Lock _syncRoot = new();
    private readonly List<Task> _pending = [];
    private CockpitConfigFileAccess? _access;
    private CockpitConfigFile? _file;
    private Task<CockpitConfigFile>? _loading;
    private FileStream? _writeGate;
    private int _depth = 1;
    private bool _dirty;
    private bool _flushed;

    private CockpitConfigWriteBatch()
    {
        _previous = _current.Value;
        _current.Value = this;
    }

    // `await using` this around the writing section of an Apply — every UpdateAsync/ReadAsync call under that
    // async flow joins the batch; disposing awaits and flushes what it collected in one round-trip. A nested
    // Begin() joins the same batch instead of re-acquiring the write gate, which is non-reentrant.
    public static CockpitConfigWriteBatch Begin()
    {
        var outer = _current.Value;
        if (outer is not null)
        {
            lock (outer._syncRoot)
            {
                outer._depth++;
            }

            return outer;
        }

        return new CockpitConfigWriteBatch();
    }

    // False (no scope, a scope for a different file, or one already flushed) means the caller falls through to
    // its own single write. `completion` resolves once this mutation is applied in memory, not once the batch has
    // written — awaiting it before the scope finishes raising its other mutations would otherwise deadlock.
    internal static bool TryApply(
        CockpitConfigFileAccess access, Action<CockpitConfigFile> mutate, CancellationToken cancellationToken, out Task completion)
    {
        var batch = _current.Value;
        if (batch is null || !batch._Owns(access))
        {
            completion = Task.CompletedTask;
            return false;
        }

        var applied = batch._ApplyAsync(access, mutate, cancellationToken);
        lock (batch._syncRoot)
        {
            // Raised after this scope already flushed (a late fire-and-forget continuation): `applied` may still
            // mutate the batch's now-discarded in-memory copy, harmlessly, but it must not be trusted as written.
            if (batch._flushed)
            {
                completion = Task.CompletedTask;
                return false;
            }

            batch._pending.Add(applied);
        }

        completion = applied;
        return true;
    }

    // Null (no scope, a scope for a different file, one that hasn't loaded anything yet, or one already flushed)
    // means the caller falls through to its own read.
    internal static CockpitConfigFile? TryPeek(CockpitConfigFileAccess access)
    {
        var batch = _current.Value;
        if (batch is null || !batch._Owns(access))
        {
            return null;
        }

        lock (batch._syncRoot)
        {
            return batch._flushed || batch._file is null ? null : CockpitConfigFileAccess.ClonePeeked(batch._file);
        }
    }

    // Guards against two CockpitConfigFileAccess instances for different paths sharing one in-memory copy —
    // production has one path, but tests use a fresh temp file per instance.
    private bool _Owns(CockpitConfigFileAccess access) =>
        _access is null || string.Equals(_access.ConfigFilePath, access.ConfigFilePath, StringComparison.Ordinal);

    private async Task _ApplyAsync(CockpitConfigFileAccess access, Action<CockpitConfigFile> mutate, CancellationToken cancellationToken)
    {
        var file = await _GetOrLoadAsync(access, cancellationToken).ConfigureAwait(false);
        lock (_syncRoot)
        {
            mutate(file);
            _dirty = true;
        }
    }

    // First mutation loads the shared copy and takes the write gate for the whole scope, not just the final
    // write — every later mutation just reuses both.
    private Task<CockpitConfigFile> _GetOrLoadAsync(CockpitConfigFileAccess access, CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            _access ??= access;
            _loading ??= _LoadAsync(access, cancellationToken);
            return _loading;
        }
    }

    private async Task<CockpitConfigFile> _LoadAsync(CockpitConfigFileAccess access, CancellationToken cancellationToken)
    {
        _writeGate = await CockpitConfigWriteGate.AcquireAsync(access.ConfigFilePath, cancellationToken).ConfigureAwait(false);
        var loaded = await access.ReadNowAsync(cancellationToken, waitForWriter: false).ConfigureAwait(false) ?? new CockpitConfigFile();
        lock (_syncRoot)
        {
            _file = loaded;
        }

        return loaded;
    }

    // Only the outermost Begin()/DisposeAsync pair actually owns the scope — a nested one just decrements back
    // below the depth it bumped, leaving `_current` and the write gate to the caller that still holds them.
    public async ValueTask DisposeAsync()
    {
        lock (_syncRoot)
        {
            if (--_depth > 0)
            {
                return;
            }
        }

        _current.Value = _previous;
        await _FlushAsync().ConfigureAwait(false);
    }

    private async Task _FlushAsync()
    {
        List<Task> pending;
        FileStream? writeGate;
        lock (_syncRoot)
        {
            if (_flushed)
            {
                return;
            }

            _flushed = true;
            pending = [.. _pending];
            writeGate = _writeGate;
        }

        // The gate must come off no matter what goes wrong below — a mutation that throws here must not leave
        // cockpit.json.lock held forever, or nothing in the process can save settings again until a restart.
        try
        {
            // AC-1085: waits out every apply this scope raised, awaited by its caller or not, before writing.
            await Task.WhenAll(pending).ConfigureAwait(false);

            CockpitConfigFileAccess? access;
            CockpitConfigFile? file;
            lock (_syncRoot)
            {
                access = _access;
                file = _dirty ? _file : null;
            }

            if (access is not null && file is not null)
            {
                await access.WriteNowAsync(file).ConfigureAwait(false);
            }
        }
        finally
        {
            writeGate?.Dispose();
        }
    }
}
