namespace Cockpit.Infrastructure.Vault;

internal sealed partial class VaultHub
{
    private readonly Dictionary<(string Provider, string Item), WriteGate> _writeGates = [];

    internal async Task<IDisposable> LockWriteAsync(string provider, string item, CancellationToken cancellationToken)
    {
        var key = (provider, item);
        WriteGate entry;
        lock (_gate)
        {
            if (!_writeGates.TryGetValue(key, out var existing))
            {
                existing = new WriteGate();
                _writeGates.Add(key, existing);
            }

            entry = existing;
            entry.Users++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _ReleaseWrite(key, entry, held: false);
            throw;
        }

        return new WriteLease(() => _ReleaseWrite(key, entry, held: true));
    }

    private void _ReleaseWrite((string Provider, string Item) key, WriteGate entry, bool held)
    {
        lock (_gate)
        {
            if (held)
            {
                entry.Semaphore.Release();
            }

            if (--entry.Users == 0)
            {
                _writeGates.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class WriteGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private sealed class WriteLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
