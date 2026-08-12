using Avalonia.Threading;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App.Services;

// AC-733: Workstation GC only returns freed heap segments to the OS when it detects memory pressure, so on a
// workstation with plenty of free RAM the process keeps holding gigabytes of garbage the GC already knows is dead —
// measured live at 10.3 GB RSS with a 6.74 GB heap that a forced collect shrank to 3.51 GB, moving RSS by ~150 MB.
// `System.GC.ConserveMemory` (runtimeconfig) addresses the *decision* to reclaim; this addresses the other half —
// an isolated repro (AC-733) showed a plain blocking Gen2 collect leaves RSS untouched regardless of that setting,
// while `GCCollectionMode.Aggressive` + `compacting: true` drops RSS to the live set within ~200-450 ms even on a
// multi-GB heap. So: ask for exactly that collect periodically, instead of waiting for real memory pressure.
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
