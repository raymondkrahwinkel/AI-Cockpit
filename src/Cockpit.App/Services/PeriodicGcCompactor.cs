using Avalonia.Threading;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App.Services;

// AC-733: Workstation GC only returns freed heap segments to the OS under real memory pressure, rare on a
// workstation with free RAM — so dead managed-heap memory piles up as RSS. ConserveMemory alone doesn't move it;
// only Aggressive+compacting does (measurements: PR #532 / AC-733). So: ask for that, periodically.
public sealed class PeriodicGcCompactor(ILogger<PeriodicGcCompactor>? logger = null) : ISingletonService, IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    // ponytail: fixed threshold, not a "how much is garbage" estimate — .NET has no cheap way to know that without
    // doing the very collect this guards against. Below this, the pause is not worth paying for what little a
    // compact could reclaim. Raise it if 15-minute hitches on a modest heap turn out to matter more than the RSS.
    private const long MinHeapBytesToCompact = 512L * 1024 * 1024;

    private readonly ILogger<PeriodicGcCompactor> _logger = logger ?? NullLogger<PeriodicGcCompactor>.Instance;

    private DispatcherTimer? _timer;
    private bool _disposed;

    // Same idiom as StaleClaimReaper: created on the UI thread once Avalonia's Setup() has bound the dispatcher,
    // called explicitly from App.axaml.cs rather than as an IHostedService for the same reason (AC-718).
    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }

        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += _OnTick;
        _timer.Start();
    }

    // One compact. Public because tests — and a live memory-verification run — drive it directly rather than
    // waiting a quarter of an hour, the same seam StaleClaimReaper.RunOnce opens for the same reason.
    public void RunOnce()
    {
        try
        {
            if (GC.GetGCMemoryInfo().HeapSizeBytes < MinHeapBytesToCompact)
            {
                return;
            }

            var before = ProcessMemoryInfo.Current().ResidentBytes;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            var after = ProcessMemoryInfo.Current().ResidentBytes;

            _logger.LogInformation(
                "Periodic GC compact freed {Freed} of resident memory ({Before} -> {After}).",
                ByteSize.Human(Math.Max(0, before - after)),
                ByteSize.Human(before),
                ByteSize.Human(after));
        }
        catch (Exception exception)
        {
            // Same discipline as every other background tick in this codebase: never take the app down over this,
            // and never go silent either — the next tick, 15 minutes from now, tries again.
            _logger.LogWarning(exception, "A periodic GC compact failed; the next one will try again.");
        }
    }

    private void _OnTick(object? sender, EventArgs e) => RunOnce();

    public void Dispose()
    {
        _disposed = true;

        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= _OnTick;
        _timer = null;
    }
}
