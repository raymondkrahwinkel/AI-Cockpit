using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Diagnostics;

namespace Cockpit.Infrastructure.Sessions;

// The `ISessionMemoryLimiter` for macOS, and since AC-692 for Windows too (replacing `WindowsJobMemoryLimiter`'s
// hard job-object kill, AC-661): neither platform enforces a cap anymore, just polls and reports. What no test
// here can cover is whether `ps` reports these figures on real macOS hardware — there is no Mac to check on (AC-57).
internal sealed class PollingMemoryLimiter : ISessionMemoryLimiter, IDisposable
{
    // Short enough to catch an ordinary build's climb, long enough that shelling out to `ps` is not the cost.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);

    private readonly IProcessTableReader _reader;
    private readonly ILogger _logger;

    // One loop and one process-table read for every watched session, rather than one `ps` per session per tick.
    private readonly ConcurrentDictionary<int, long> _watched = new();

    // One-shot per crossing, same shape as every other memory warning in this codebase — otherwise a session
    // sitting over its cap logs the same warning every 1.5 seconds for as long as it stays there.
    private readonly ConcurrentDictionary<int, bool> _reported = new();

    private readonly CancellationTokenSource _stopped = new();
    private Task? _loop;
    private readonly Lock _loopLock = new();

    public PollingMemoryLimiter(IProcessTableReader reader, ILogger<PollingMemoryLimiter> logger)
    {
        _reader = reader;
        _logger = logger;
    }

    public IDisposable? Apply(int processId, long capBytes)
    {
        _watched[processId] = capBytes;
        _EnsureRunning();
        _logger.LogInformation("Session {ProcessId} watched against a {CapBytes} byte cap (best effort).", processId, capBytes);
        return new Watch(this, processId);
    }

    // One sweep: every watched session whose tree is over its cap is logged once, on the way up — the same crossing
    // that used to end in a kill (AC-692). Returns how many were newly reported, which is what the tests assert
    // on — no timer, no waiting.
    internal int CheckOnce()
    {
        if (_watched.IsEmpty)
        {
            return 0;
        }

        var rows = _reader.Read();
        var reported = 0;

        foreach (var (processId, capBytes) in _watched)
        {
            var held = ProcessTree.Sum(rows, processId).WorkingSetBytes;
            if (held <= capBytes)
            {
                _reported.TryRemove(processId, out _);
                continue;
            }

            if (!_reported.TryAdd(processId, true))
            {
                continue;
            }

            _logger.LogWarning(
                "Session {ProcessId} held {HeldBytes} bytes against its {CapBytes} byte cap. Not stopped automatically (AC-692) — the operator decides from the cockpit's own notice.",
                processId,
                held,
                capBytes);

            reported++;
        }

        return reported;
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

    private sealed class Watch(PollingMemoryLimiter limiter, int processId) : IDisposable
    {
        public void Dispose()
        {
            limiter._watched.TryRemove(processId, out _);
            limiter._reported.TryRemove(processId, out _);
        }
    }
}
