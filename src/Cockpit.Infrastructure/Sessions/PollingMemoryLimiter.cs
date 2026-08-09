using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Diagnostics;

namespace Cockpit.Infrastructure.Sessions;

// macOS `ISessionMemoryLimiter` (AC-661): no cgroups and no job objects, so the tree's RSS is polled and killed
// when it goes over. `setrlimit(RLIMIT_AS)` bounds address space, which .NET and Node reserve far more of than
// they use; Jetsam has no public per-child-tree API. Weaker than a kernel cap, said plainly: a spike between two
// polls can get through. `PollingMemoryLimiterTests` covers the logic against a fake process table; what no test
// here can cover is whether `ps` reports these figures on real macOS hardware, since there is no Mac to check on
// (the same blind spot as AC-57).
internal sealed class PollingMemoryLimiter : ISessionMemoryLimiter, IDisposable
{
    // Short enough to catch an ordinary build's climb, long enough that shelling out to `ps` is not the cost.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);

    private readonly IProcessTableReader _reader;
    private readonly ILogger _logger;
    private readonly Action<int> _kill;

    // One loop and one process-table read for every watched session, rather than one `ps` per session per tick.
    private readonly ConcurrentDictionary<int, long> _watched = new();
    private readonly CancellationTokenSource _stopped = new();
    private Task? _loop;
    private readonly Lock _loopLock = new();

    public PollingMemoryLimiter(IProcessTableReader reader, ILogger<PollingMemoryLimiter> logger)
        : this(reader, logger, KillTree)
    {
    }

    // Test seam: the kill is what a test must not actually perform.
    internal PollingMemoryLimiter(IProcessTableReader reader, ILogger logger, Action<int> kill)
    {
        _reader = reader;
        _logger = logger;
        _kill = kill;
    }

    public IDisposable? Apply(int processId, long capBytes)
    {
        _watched[processId] = capBytes;
        _EnsureRunning();
        _logger.LogInformation("Session {ProcessId} watched against a {CapBytes} byte cap (best effort).", processId, capBytes);
        return new Watch(this, processId);
    }

    // One sweep: every watched session whose tree is over its cap is killed and dropped. Returns how many were
    // killed, which is what the tests assert on — no timer, no waiting.
    internal int CheckOnce()
    {
        if (_watched.IsEmpty)
        {
            return 0;
        }

        var rows = _reader.Read();
        var killed = 0;

        foreach (var (processId, capBytes) in _watched)
        {
            var held = ProcessTree.Sum(rows, processId).WorkingSetBytes;
            if (held <= capBytes)
            {
                continue;
            }

            _logger.LogWarning(
                "Session {ProcessId} held {HeldBytes} bytes against its {CapBytes} byte cap; stopping it so the cockpit is not taken with it.",
                processId,
                held,
                capBytes);

            // Dropped before the kill, so a slow kill cannot be counted twice on the next sweep.
            _watched.TryRemove(processId, out _);
            killed++;

            try
            {
                _kill(processId);
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or SystemException)
            {
                _logger.LogWarning(exception, "Session {ProcessId} could not be stopped.", processId);
            }
        }

        return killed;
    }

    public void Dispose()
    {
        _stopped.Cancel();
        _stopped.Dispose();
    }

    private void _EnsureRunning()
    {
        lock (_loopLock)
        {
            _loop ??= Task.Run(_PollAsync);
        }
    }

    private async Task _PollAsync()
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopped.Token).ConfigureAwait(false))
            {
                CheckOnce();
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // The cockpit is shutting down; the token is cancelled and then disposed under this loop.
        }
    }

    // SIGKILL to the tree: a session that is over its cap has already lost, and a polite signal it can ignore
    // would leave the machine in exactly the state this exists to prevent.
    private static void KillTree(int processId)
    {
        using var process = Process.GetProcessById(processId);
        process.Kill(entireProcessTree: true);
    }

    private sealed class Watch(PollingMemoryLimiter limiter, int processId) : IDisposable
    {
        public void Dispose() => limiter._watched.TryRemove(processId, out _);
    }
}
