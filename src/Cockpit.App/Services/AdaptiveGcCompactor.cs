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
// its view tree + ~66 composition visuals/row behind; measured at 967 orphaned views for 216 live rows, ~64k
// composition visuals over one streaming ticket). Our side detaches cleanly (the views end up with a null parent).
// Recheck on an Avalonia upgrade — the real fix belongs there, and this compactor can likely shrink or go once it
// lands. Full repro/analysis was captured under cockpit-diag (VERIFY-fixed.md).
public sealed class AdaptiveGcCompactor(
    ILogger<AdaptiveGcCompactor>? logger = null,
    Func<long>? heapBytesProbe = null,
    Action? compact = null,
    Func<DateTime>? utcNow = null) : ISingletonService, IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(200);

    // Compact once the heap crosses this — low enough that catching it here keeps the pause under ~100 ms even
    // under a deliberately aggressive multi-thread leak simulation (AC-733).
    private const long CompactThresholdBytes = 300L * 1024 * 1024;

    // AC-756: how much the heap must grow past what the previous compact settled at before the next one is worth
    // its pause. A threshold on the level alone compacts forever once the *live* set sits above it — measured on
    // Raymond's instance: 133 compacts/minute of ~250 ms each, freeing under 1 MB, for 40 minutes.
    private const long GrowthBeforeNextCompactBytes = 100L * 1024 * 1024;

    // ponytail: belt-and-suspenders, not the primary defense — the frequent check above should never let the
    // heap get anywhere near this. If it ever does (a stalled monitor thread, extreme system load), skip the
    // blocking compact rather than risk the multi-minute pause measured on an uncontrolled multi-GB heap.
    private const long MaxSafeHeapBytesToCompact = 3L * 1024 * 1024 * 1024;

    // AC-770: above the ceiling, retry at most this often (still bounded pause risk) instead of never again —
    // and log at most this often too, so an unresolved leak that camps above the ceiling can't flood the log.
    private static readonly TimeSpan OverCeilingLogInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan OverCeilingCompactCooldown = TimeSpan.FromSeconds(30);

    private readonly ILogger<AdaptiveGcCompactor> _logger = logger ?? NullLogger<AdaptiveGcCompactor>.Instance;

    // Both injectable so a test can drive the growth gate without growing a real heap past 300 MB, and without
    // firing a real blocking gen2 collect inside a shared test process (the load-hurts-the-neighbours trap).
    private readonly Func<long> _heapBytes = heapBytesProbe ?? (() => GC.GetGCMemoryInfo().HeapSizeBytes);

    private readonly Action _compact = compact ?? (() => GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true));

    // Injectable so a test can drive the cooldown/rate-limit without sleeping for real.
    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);

    // Only ever read and written by the one monitor thread (or a test driving CheckOnce directly).
    private long _compactAtBytes = CompactThresholdBytes;
    private DateTime? _overCeilingSince;
    private DateTime? _lastOverCeilingWarnUtc;
    private DateTime? _lastOverCeilingCompactAttemptUtc;
    private bool _loggedOverCeilingError;

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

            if (heapBytes <= MaxSafeHeapBytesToCompact)
            {
                _overCeilingSince = null;
                _loggedOverCeilingError = false;
            }

            if (heapBytes < _compactAtBytes)
            {
                return;
            }

            if (heapBytes > MaxSafeHeapBytesToCompact)
            {
                var now = _utcNow();
                _overCeilingSince ??= now;

                if (!_loggedOverCeilingError)
                {
                    if (now - _overCeilingSince.Value >= OverCeilingLogInterval)
                    {
                        _logger.LogError(
                            "Managed heap has stayed above the safe compact ceiling ({Heap}) for over a minute — probable leak, a heap dump is recommended.",
                            ByteSize.Human(heapBytes));
                        _loggedOverCeilingError = true;
                    }
                    else if (_lastOverCeilingWarnUtc is null || now - _lastOverCeilingWarnUtc.Value >= OverCeilingLogInterval)
                    {
                        _logger.LogWarning(
                            "Managed heap reached {Heap}, past the safe compact ceiling — skipping an automatic compact to avoid a long pause.",
                            ByteSize.Human(heapBytes));
                        _lastOverCeilingWarnUtc = now;
                    }
                }

                // AC-770: don't latch forever — retry a bounded compact attempt once the cooldown elapses, since
                // a heap stuck above the ceiling never recovers on its own if it never compacts again.
                if (_lastOverCeilingCompactAttemptUtc is not null && now - _lastOverCeilingCompactAttemptUtc.Value < OverCeilingCompactCooldown)
                {
                    return;
                }

                _lastOverCeilingCompactAttemptUtc = now;
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

    private void _Run()
    {
        while (!_stopping)
        {
            CheckOnce();
            Thread.Sleep(CheckInterval);
        }
    }

    public void Dispose() => _stopping = true;
}
