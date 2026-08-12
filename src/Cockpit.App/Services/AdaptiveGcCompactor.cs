using Cockpit.Core.Abstractions;
using Cockpit.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App.Services;

// AC-733: catches a heap that grows large fast (seconds, not the hours the original ticket took) by checking
// cheaply and often (~0.2 microseconds/check, measured) instead of waiting on a slow timer, so a compact catches
// the heap small and cheap (tens of ms) instead of large and catastrophic (283 s measured at ~24M objects).
public sealed class AdaptiveGcCompactor(ILogger<AdaptiveGcCompactor>? logger = null) : ISingletonService, IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(200);

    // Compact once the heap crosses this — low enough that catching it here keeps the pause under ~100 ms even
    // under a deliberately aggressive multi-thread leak simulation (AC-733).
    private const long CompactThresholdBytes = 300L * 1024 * 1024;

    // ponytail: belt-and-suspenders, not the primary defense — the frequent check above should never let the
    // heap get anywhere near this. If it ever does (a stalled monitor thread, extreme system load), skip the
    // blocking compact rather than risk the multi-minute pause measured on an uncontrolled multi-GB heap.
    private const long MaxSafeHeapBytesToCompact = 3L * 1024 * 1024 * 1024;

    private readonly ILogger<AdaptiveGcCompactor> _logger = logger ?? NullLogger<AdaptiveGcCompactor>.Instance;

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
            var heapBytes = GC.GetGCMemoryInfo().HeapSizeBytes;
            if (heapBytes < CompactThresholdBytes)
            {
                return;
            }

            if (heapBytes > MaxSafeHeapBytesToCompact)
            {
                _logger.LogWarning(
                    "Managed heap reached {Heap}, past the safe compact ceiling — skipping an automatic compact to avoid a long pause.",
                    ByteSize.Human(heapBytes));
                return;
            }

            var before = ProcessMemoryInfo.Current().ResidentBytes;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            var after = ProcessMemoryInfo.Current().ResidentBytes;

            _logger.LogInformation(
                "GC compact (heap was {Heap}) freed {Freed} of resident memory ({Before} -> {After}).",
                ByteSize.Human(heapBytes),
                ByteSize.Human(Math.Max(0, before - after)),
                ByteSize.Human(before),
                ByteSize.Human(after));
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
