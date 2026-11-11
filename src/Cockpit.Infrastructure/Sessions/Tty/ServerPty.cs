using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions.Tty;

// AC-2288: one pty on this server for a remote window. Its output is kept in a bounded replay so a reattach repaints,
// and read by one reader at a time, so a reconnect takes the stream over instead of opening a second pty.
// While a reader is attached the pump waits for it (backpressure reaches the process); without one, the oldest bytes go.
internal sealed class ServerPty : IAsyncDisposable
{
    public const int ReplayBytes = 256 * 1024;
    public const int MaxInputBytes = 64 * 1024;

    private readonly IConPtyProcess _process;
    private readonly byte[] _ring = new byte[ReplayBytes];
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _input = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _written;
    private long _readerAt;
    private CancellationTokenSource? _reader;
    private bool _exited;
    private int _disposed;

    public ServerPty(IConPtyProcess process)
    {
        _process = process;
        _pump = Task.Run(_PumpAsync);
    }

    public bool HasExited
    {
        get
        {
            lock (_gate)
            {
                return _exited;
            }
        }
    }

    public bool HasReader
    {
        get
        {
            lock (_gate)
            {
                return _reader is not null;
            }
        }
    }

    // Raised once, when the process ended or the pty was closed.
    public event Action? Exited;

    // Takes the stream from any earlier reader and writes from `after` (or the oldest byte still kept) until the process
    // ends, `cancellationToken` fires or another reader takes over.
    public async Task CopyToAsync(Stream destination, long? after, CancellationToken cancellationToken)
    {
        using var mine = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long at;
        lock (_gate)
        {
            _reader?.Cancel();
            _reader = mine;
            at = Math.Clamp(after ?? 0, Math.Max(0, _written - ReplayBytes), _written);
            _readerAt = at;
            _SignalLocked();
        }

        var chunk = new byte[16 * 1024];
        try
        {
            while (true)
            {
                Task changed;
                var count = 0;
                lock (_gate)
                {
                    // Fell behind the replay while detached: resume from the oldest byte kept.
                    at = Math.Max(at, _written - ReplayBytes);
                    if (at < _written)
                    {
                        count = (int)Math.Min(chunk.Length, _written - at);
                        var start = (int)(at % ReplayBytes);
                        var first = Math.Min(count, ReplayBytes - start);
                        Array.Copy(_ring, start, chunk, 0, first);
                        Array.Copy(_ring, 0, chunk, first, count - first);
                    }
                    else if (_exited)
                    {
                        return;
                    }

                    changed = _changed.Task;
                }

                if (count == 0)
                {
                    await changed.WaitAsync(mine.Token).ConfigureAwait(false);
                    continue;
                }

                await destination.WriteAsync(chunk.AsMemory(0, count), mine.Token).ConfigureAwait(false);
                await destination.FlushAsync(mine.Token).ConfigureAwait(false);
                at += count;
                lock (_gate)
                {
                    _readerAt = at;
                    _SignalLocked();
                }
            }
        }
        catch (OperationCanceledException) when (mine.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_reader, mine))
                {
                    _reader = null;
                    _SignalLocked();
                }
            }
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await _input.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (HasExited)
            {
                return;
            }

            await _process.InputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _process.InputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _input.Release();
        }
    }

    public void Resize(int columns, int rows)
    {
        if (!HasExited)
        {
            _process.Resize((short)Math.Clamp(columns, 2, 500), (short)Math.Clamp(rows, 2, 300));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        _process.Dispose();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }

        _stop.Dispose();
    }

    private async Task _PumpAsync()
    {
        var buffer = new byte[8 * 1024];
        try
        {
            while (true)
            {
                var read = await _process.OutputStream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await _WaitForRoomAsync(read).ConfigureAwait(false);
                lock (_gate)
                {
                    var start = (int)(_written % ReplayBytes);
                    var first = Math.Min(read, ReplayBytes - start);
                    Array.Copy(buffer, 0, _ring, start, first);
                    Array.Copy(buffer, first, _ring, 0, read - first);
                    _written += read;
                    _SignalLocked();
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            lock (_gate)
            {
                _exited = true;
                _SignalLocked();
            }

            Exited?.Invoke();
        }
    }

    private async Task _WaitForRoomAsync(int incoming)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_reader is null || _written + incoming - _readerAt <= ReplayBytes)
                {
                    return;
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
    }

    private void _SignalLocked()
    {
        var changed = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
    }
}
