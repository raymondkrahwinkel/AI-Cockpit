using System.Diagnostics;
using Cockpit.MeasurementHarness.Core;

namespace Cockpit.MeasurementHarness.Meters;

/// <summary>
/// The process's share of the machine, over a window this instance owns. Cockpit's own `_CpuSampler` is a
/// single shared instance for two callers and every call resets its baseline: at the same 0,96-core load
/// the hang line reads 4,4% with snapshots on and 0,0% with them off, because that is its first call.
/// </summary>
public sealed class CpuMeter
{
    private TimeSpan? _baselineCpu;
    private long _baselineTimestamp;

    /// <summary>Opens this meter's own window. Nobody else's reading moves it.</summary>
    public void Start()
    {
        _baselineCpu = Process.GetCurrentProcess().TotalProcessorTime;
        _baselineTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Share of the machine since <see cref="Start"/>, or null when there is no baseline yet. Null rather
    /// than zero on purpose: `0` is a measurement and `n/a` is the truth, and the two read very differently
    /// in a report about an app that is supposedly doing nothing.
    /// </summary>
    public double? Percent()
    {
        if (_baselineCpu is not { } baseline)
        {
            return null;
        }

        var elapsed = Stopwatch.GetElapsedTime(_baselineTimestamp);
        if (elapsed <= TimeSpan.Zero)
        {
            return null;
        }

        var used = Process.GetCurrentProcess().TotalProcessorTime - baseline;
        return used.TotalSeconds / (elapsed.TotalSeconds * Environment.ProcessorCount) * 100.0;
    }

    /// <summary>The reading as it belongs in a report, with no baseline spelled out rather than implied.</summary>
    public string Line(string label) =>
        Percent() is { } percent ? $"{label}: {percent:F1}% of {Environment.ProcessorCount} cores" : $"{label}: n/a (no baseline)";
}

/// <summary>
/// What the garbage collector actually cost, over a window this instance owns. `gcpause` on the diag line
/// is a window figure that freezes as soon as no collection runs; the cumulative clock is the honest one.
/// </summary>
public sealed class GcMeter
{
    private TimeSpan _baselinePause;
    private int[] _baselineCollections = [];

    public void Start()
    {
        _baselinePause = GC.GetTotalPauseDuration();
        _baselineCollections = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
    }

    /// <summary>Time the process spent stopped for collection since <see cref="Start"/>.</summary>
    public TimeSpan PauseSince() => GC.GetTotalPauseDuration() - _baselinePause;

    public int CollectionsSince(int generation) =>
        _baselineCollections.Length > generation ? GC.CollectionCount(generation) - _baselineCollections[generation] : 0;

    /// <summary>
    /// Bytes still reachable after a full blocking collection. Refused outside the verification phase: this
    /// call took 17,1 s on a 10,2 GB heap and set off the app's own uifreeze detector from inside a
    /// measurement window (E3).
    /// </summary>
    public long ReachableBytes(MeasurementRun run)
    {
        if (run.Phase != Phase.Verify)
        {
            throw new InvalidOperationException(
                "ReachableBytes is a verification step, not a measurement. Move it into run.Verify(...): a forced "
                + "full GC costs seconds on a large heap and shows up as a freeze in whatever is being measured.");
        }

        return GC.GetTotalMemory(forceFullCollection: true);
    }

    public string Line(string label) =>
        $"{label}: {PauseSince().TotalMilliseconds:F1} ms paused, gen0/1/2 = "
        + $"{CollectionsSince(0)}/{CollectionsSince(1)}/{CollectionsSince(2)}";
}
