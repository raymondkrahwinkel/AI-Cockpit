using Cockpit.Core.Abstractions;
using Cockpit.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App.Services;

// AC-733: checks cheaply/often (~0.2us) instead of a slow timer, catching heap growth small (tens of ms) not
// catastrophic (283 s at ~24M objects). ponytail: growth is mostly Avalonia 12.1.1 retaining detached views live
// and rooted (compaction only reclaims ~2% RSS); only compacts below CompactCeilingBytes — see cockpit-diag VERIFY-fixed.md.
public sealed class AdaptiveGcCompactor(
    ILogger<AdaptiveGcCompactor>? logger = null,
    Func<long>? heapBytesProbe = null,
    Action? compact = null,
    Func<DateTime>? utcNow = null,
    Func<long>? allocatedBytesProbe = null) : ISingletonService, IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(200);

    // A compact still stops the world, so even a sub-200 ms one is a visible micro-stutter mid-keystroke/scroll/stream.
    // Only compact in a lull: low recent allocation means the pause overlaps time nobody is watching, while active
    // work (streaming, scrolling) defers it. This only decides *when* the harmless small-heap compacts happen.
    private const long QuietAllocBytesPerCheck = 8L * 1024 * 1024;

    // Compact once the heap crosses this — low enough that catching it here keeps the pause under ~100 ms even
    // under a deliberately aggressive multi-thread leak simulation (AC-733).
    private const long CompactThresholdBytes = 300L * 1024 * 1024;

    // AC-756: how much the heap must grow past what the previous compact settled at before the next one is worth
    // its pause. A threshold on the level alone compacts forever once the *live* set sits above it — measured on
    // Raymond's instance: 133 compacts/minute of ~250 ms each, freeing under 1 MB, for 40 minutes.
    private const long GrowthBeforeNextCompactBytes = 100L * 1024 * 1024;

    // Never compact above this: pause scales with heap size (~100ms at the 300MB floor, 5-12s at ~4GB — every
    // Rick/Fedora and Raymond/Windows UI-freeze traced to a compact at 3.7-4.1 GB). Above it the live, rooted
    // Avalonia retention compaction can't free (~2% RSS reclaimed) anyway, so raise/lower this as the pause-budget knob.
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

    // Throttled operator warning for a heap well past any legitimate working set, escalating to a single error
    // after staying up over a minute. No compact happens here — at this size the pause would be a multi-second
    // freeze for a live leak compaction can't reclaim, so a restart, not a compact, is the remedy.
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
