using Cockpit.Core.Abstractions;
using Cockpit.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App.Services;

// AC-733: catches a heap that grows large fast (seconds, not the hours the original ticket took) by checking
// cheaply and often (~0.2 microseconds/check, measured) instead of waiting on a slow timer, so a compact catches
// the heap small and cheap (tens of ms) instead of large and catastrophic (283 s measured at ~24M objects).
//
// ponytail: the growth this compacts is largely an Avalonia 12.1.1 issue, not our own — the compositor keeps
// detached transcript-row views rooted (a VirtualizingStackPanel dematerialising a row, or a pane closing, leaves
// its view tree + composition visuals behind). Our side detaches cleanly (the views end up with a null parent).
// That growth is live and rooted, so compaction cannot free it — it only hands emptied segments back to the OS,
// measured at ~2% of RSS (100 MB off a 4.6 GB heap). Recheck on an Avalonia upgrade; the real fix belongs there.
//
// Hard-won (Raymond on Windows + Rick on Fedora, 2026-08-15): a blocking compacting gen2 collect of a MULTI-GB
// live heap is a 5-12 s stop-the-world pause — every one of Rick's logged UI-freeze hangs timestamps to one of
// these compacts. So this only compacts while the heap is still small enough for the pause to be a hitch
// (CompactCeilingBytes); above that it leaves the heap alone and tells the operator a restart is what clears it.
// Full repro/analysis under cockpit-diag (VERIFY-fixed.md).
public sealed class AdaptiveGcCompactor(
    ILogger<AdaptiveGcCompactor>? logger = null,
    Func<long>? heapBytesProbe = null,
    Action? compact = null,
    Func<DateTime>? utcNow = null,
    Func<long>? allocatedBytesProbe = null) : ISingletonService, IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(200);

    // A compact still stops the world for its duration, so even a sub-200 ms one is a visible micro-stutter if it
    // lands mid-keystroke, mid-scroll or mid-stream. So only compact in a lull: when allocation over the last check
    // was low, the app is idle and the pause overlaps time nobody is watching. Active work (streaming a reply,
    // scrolling the transcript) allocates far more than this per 200 ms tick, so a compact is deferred until it
    // stops — which is what keeps the GC from ever being felt. The leak still can't be compacted away either way;
    // this just decides *when* the harmless small-heap compacts happen.
    private const long QuietAllocBytesPerCheck = 8L * 1024 * 1024;

    // Compact once the heap crosses this — low enough that catching it here keeps the pause under ~100 ms even
    // under a deliberately aggressive multi-thread leak simulation (AC-733).
    private const long CompactThresholdBytes = 300L * 1024 * 1024;

    // AC-756: how much the heap must grow past what the previous compact settled at before the next one is worth
    // its pause. A threshold on the level alone compacts forever once the *live* set sits above it — measured on
    // Raymond's instance: 133 compacts/minute of ~250 ms each, freeing under 1 MB, for 40 minutes.
    private const long GrowthBeforeNextCompactBytes = 100L * 1024 * 1024;

    // Never compact above this. A blocking compacting gen2 collect relocates the whole live set, so its pause
    // scales with heap size: ~100 ms at the 300 MB floor, but 5-12 s at ~4 GB (measured cross-platform — every one
    // of Rick's UI-freeze hangs on Fedora timestamps to a compact at 3.7-4.1 GB, and Raymond's Windows log shows
    // the same). Kept close to the floor so every compact we do run stays a sub-200 ms hitch rather than a freeze;
    // above it the growth is the live, rooted Avalonia retention compaction can't free anyway (it returned ~2% of
    // RSS), so we leave the heap alone. This is the pause-budget knob: raise it to reclaim more RSS in exchange for
    // a longer per-compact pause, lower it if even this hitches on slower hardware.
    private const long CompactCeilingBytes = 512L * 1024 * 1024;

    // Well past any legitimate working set: tell the operator a restart is what clears it (the underlying leak is
    // Avalonia's, not ours — see the class note). Throttled so a heap that camps up here can't flood the log.
    private const long LeakWarnCeilingBytes = 3L * 1024 * 1024 * 1024;
    private static readonly TimeSpan LeakWarnInterval = TimeSpan.FromMinutes(1);

    private readonly ILogger<AdaptiveGcCompactor> _logger = logger ?? NullLogger<AdaptiveGcCompactor>.Instance;

    // Both injectable so a test can drive the growth gate without growing a real heap past 300 MB, and without
    // firing a real blocking gen2 collect inside a shared test process (the load-hurts-the-neighbours trap).
    private readonly Func<long> _heapBytes = heapBytesProbe ?? (() => GC.GetGCMemoryInfo().HeapSizeBytes);

    private readonly Action _compact = compact ?? (() => GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true));

    // Injectable so a test can drive the cooldown/rate-limit without sleeping for real.
    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);

    // Cumulative bytes ever allocated — monotonic, so its delta between checks is a clean allocation-rate signal
    // (the heap size itself is not: a collection drops it mid-stream). Injectable so a test can drive "busy" vs
    // "idle" without allocating for real. precise:false keeps it a cheap per-thread sum.
    private readonly Func<long> _allocatedBytes = allocatedBytesProbe ?? DefaultAllocatedBytes;

    // Only ever read and written by the one monitor thread (or a test driving CheckOnce directly). Baselined at
    // construction so the first check measures a real delta rather than the whole process's allocations to date.
    private long _compactAtBytes = CompactThresholdBytes;
    private long _lastAllocatedBytes = (allocatedBytesProbe ?? DefaultAllocatedBytes)();
    private DateTime? _leakWarnSince;
    private DateTime? _lastLeakWarnUtc;
    private bool _loggedLeakError;

    private Thread? _thread;
    private volatile bool _stopping;

    // Plain background thread, not a DispatcherTimer — this never touches the UI thread, so unlike
    // DiagnosticsBackgroundService/StaleClaimReaper it does not need Avalonia's dispatcher bound first.
    public void Start()
    {
        if (_thread is not null || _stopping)
        {
            return;
        }

        _thread = new Thread(_Run) { IsBackground = true, Name = "cockpit-gc-compact" };
        _thread.Start();
    }

    // One check. Public so tests drive it directly — same seam as StaleClaimReaper.RunOnce.
    public void CheckOnce()
    {
        try
        {
            var heapBytes = _heapBytes();

            // Sample the allocation rate every check so it stays accurate whichever branch returns below.
            var allocated = _allocatedBytes();
            var appIsBusy = allocated - _lastAllocatedBytes >= QuietAllocBytesPerCheck;
            _lastAllocatedBytes = allocated;

            // Reset the leak-warning latch once the heap falls back under the alarm ceiling.
            if (heapBytes <= LeakWarnCeilingBytes)
            {
                _leakWarnSince = null;
                _loggedLeakError = false;
            }

            // Above the compact ceiling, don't: the blocking compacting collect would stop the world for seconds
            // (this is the freeze both Raymond and Rick were seeing) to reclaim almost nothing, since the growth up
            // here is the live, rooted Avalonia retention compaction can't free. Warn the operator instead.
            if (heapBytes >= CompactCeilingBytes)
            {
                if (heapBytes >= LeakWarnCeilingBytes)
                {
                    _WarnAboutProbableLeak(heapBytes);
                }

                return;
            }

            if (heapBytes < _compactAtBytes)
            {
                return;
            }

            // Defer to a lull. Compacting while the app is actively allocating (streaming a reply, scrolling the
            // transcript) is exactly when the pause would be seen; the heap will still be here at the next quiet
            // check, so nothing is lost by waiting for one.
            if (appIsBusy)
            {
                return;
            }

            var before = ProcessMemoryInfo.Current().ResidentBytes;
            _compact();
            var after = ProcessMemoryInfo.Current().ResidentBytes;

            // What is left standing after a compact is live, so it will still be there on the next check — arm the
            // next compact above it rather than against the bare floor.
            _compactAtBytes = Math.Max(CompactThresholdBytes, _heapBytes() + GrowthBeforeNextCompactBytes);

            _logger.LogInformation(
                "GC compact (heap was {Heap}) freed {Freed} of resident memory ({Before} -> {After}); next compact at {Next}.",
                ByteSize.Human(heapBytes),
                ByteSize.Human(Math.Max(0, before - after)),
                ByteSize.Human(before),
                ByteSize.Human(after),
                ByteSize.Human(_compactAtBytes));
        }
        catch (Exception exception)
        {
            // Same discipline as every other background tick in this codebase: never take the app down over
            // this, and never go silent either — the next check, 200 ms from now, tries again.
            _logger.LogWarning(exception, "A GC heap check failed; the next one will try again.");
        }
    }

    // Throttled operator warning for a heap that has climbed well past any legitimate working set. Escalates to a
    // single error once it has stayed up there for over a minute. No compact happens here — see CheckOnce: at this
    // size the pause would be a multi-second freeze for a live leak compaction can't reclaim, so a restart, not a
    // compact, is the remedy.
    private void _WarnAboutProbableLeak(long heapBytes)
    {
        if (_loggedLeakError)
        {
            return;
        }

        var now = _utcNow();
        _leakWarnSince ??= now;

        if (now - _leakWarnSince.Value >= LeakWarnInterval)
        {
            _logger.LogError(
                "Managed heap has stayed above {Ceiling} for over a minute ({Heap}) — a known Avalonia retention leak; a restart is what clears it.",
                ByteSize.Human(LeakWarnCeilingBytes),
                ByteSize.Human(heapBytes));
            _loggedLeakError = true;
        }
        else if (_lastLeakWarnUtc is null || now - _lastLeakWarnUtc.Value >= LeakWarnInterval)
        {
            _logger.LogWarning(
                "Managed heap reached {Heap}, past {Ceiling} — not compacting (the pause would outweigh the little it reclaims); a restart clears it.",
                ByteSize.Human(heapBytes),
                ByteSize.Human(LeakWarnCeilingBytes));
            _lastLeakWarnUtc = now;
        }
    }

    private void _Run()
    {
        while (!_stopping)
        {
            CheckOnce();
            Thread.Sleep(CheckInterval);
        }
    }

    // Named so the field and its baseline can share one default without duplicating the GC call.
    private static long DefaultAllocatedBytes() => GC.GetTotalAllocatedBytes(precise: false);

    public void Dispose() => _stopping = true;
}
