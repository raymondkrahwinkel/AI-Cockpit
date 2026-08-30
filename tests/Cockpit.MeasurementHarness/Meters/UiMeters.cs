using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace Cockpit.MeasurementHarness.Meters;

/// <summary>
/// Frame cadence and layout rounds per frame. The freeze under investigation made frames of 7 to 15 ms for
/// an hour rather than one block of five seconds, so a "does it still respond" yardstick misses it by
/// construction — which is how the old headless harness gave seven greens on a fault that was there.
/// </summary>
public sealed class FrameMeter
{
    private readonly List<double> _intervals = [];
    private readonly List<int> _roundsPerFrame = [];
    private readonly List<FrameCost> _costs = [];
    private long _lastFrame;
    private long _lastAllocated;
    private bool _attached;

    /// <summary>Frame sequence number, so a measurement can say "in this same frame" instead of approximating it with a time window.</summary>
    public int Ordinal { get; private set; }

    public double Scaling { get; private set; }

    public bool LayoutRounding { get; private set; }

    public int FrameCount => _intervals.Count;

    /// <summary>
    /// Starts the heartbeat. `RequestAnimationFrame` comes off the media context's own clock, and asking for
    /// the next frame schedules a render — so this measures a drawing app, which is the state an operator
    /// watching a streaming session is actually in.
    /// </summary>
    public void Attach(TopLevel top)
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        Scaling = top.RenderScaling;
        LayoutRounding = top.UseLayoutRounding;

        void Tick(TimeSpan _)
        {
            var now = Stopwatch.GetTimestamp();

            // Closes out the frame that was current until this tick, so its wall time and its allocation carry
            // the same ordinal the rounds below are counted under. Ordinal only moves after that.
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            if (_lastFrame != 0)
            {
                var elapsed = Stopwatch.GetElapsedTime(_lastFrame, now).TotalMilliseconds;
                _intervals.Add(elapsed);
                _costs.Add(new FrameCost(Ordinal, elapsed, allocated - _lastAllocated));
            }

            Ordinal++;
            _lastFrame = now;
            _lastAllocated = allocated;
            top.RequestAnimationFrame(Tick);
        }

        top.RequestAnimationFrame(Tick);

        // Layoutable.LayoutUpdated is raised once per ExecuteLayoutPass, so this counts rounds. Rounds are
        // what runs into Avalonia's cut-off of 153 — several offset writes inside one round fold together,
        // because QueueLayoutPass does nothing while a pass is already queued.
        top.LayoutUpdated += (_, _) => _roundsPerFrame.Add(Ordinal);
    }

    /// <summary>
    /// Highest number of layout rounds any single frame needed — the figure that runs into 153. Null when
    /// the frame clock never pulsed: without frame boundaries every round falls in the same bucket, which
    /// would read as one enormous frame rather than as the blind measurement it is.
    /// </summary>
    public int? MaxRoundsInAFrame() =>
        FrameCount == 0 || _roundsPerFrame.Count == 0 ? null : _roundsPerFrame.GroupBy(o => o).Max(g => g.Count());

    public int TotalRounds => _roundsPerFrame.Count;

    /// <summary>
    /// What the frames that needed at least <paramref name="rounds"/> rounds actually cost, against the frames
    /// that stayed under it. AC-1104 established that the cut-off is reached but never what reaching it costs,
    /// and a rounds-per-frame count alone cannot say: 153 rounds inside a 12 ms frame is a different app from
    /// 153 rounds inside a 300 ms one. Null when no frame reached the threshold — there is nothing to price.
    /// </summary>
    public FrameCostSummary? CostOfFramesAtOrAbove(int rounds) =>
        FrameCostSummary.Of(_costs, _roundsPerFrame, rounds);

    public double Percentile(double p)
    {
        if (_intervals.Count == 0)
        {
            return 0;
        }

        var sorted = _intervals.Order().ToList();
        var index = (int)Math.Clamp(Math.Round(p / 100.0 * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[index];
    }

    public IEnumerable<string> Lines()
    {
        yield return $"render scaling {Scaling:F3}, layout rounding {LayoutRounding}";
        yield return _intervals.Count == 0
            ? "frames: none seen — the clock never pulsed, so this is a blind run and not a smooth app"
            : $"frames: {_intervals.Count}, p50 {Percentile(50):F2} ms, p99 {Percentile(99):F2} ms, max {Percentile(100):F2} ms";
        yield return $"layout rounds: {TotalRounds} total, worst frame "
                     + $"{(MaxRoundsInAFrame() is { } worst ? worst.ToString() : "n/a (no frame clock)")} (Avalonia cuts off at 153)";
    }
}

/// <summary>What one frame cost: the wall time it occupied and what the UI thread allocated inside it.</summary>
public readonly record struct FrameCost(int Ordinal, double Milliseconds, long AllocatedBytes);

/// <summary>
/// The price of the frames that ran into the cut-off: how many there were, how long they took, and what they
/// allocated per round — each against the frames of the same run that stayed under it.
/// </summary>
public sealed record FrameCostSummary(
    int Frames,
    int FramesTotal,
    double ShortestMs,
    double LongestMs,
    double TotalMs,
    double? AverageOtherFrameMs,
    long AllocatedBytes,
    long AllocatedBytesPerRound,
    long? OtherAllocatedBytesPerRound)
{
    /// <summary>
    /// Prices the frames that needed at least <paramref name="rounds"/> rounds against the ones that did not.
    /// Takes the samples rather than reading a meter, so the arithmetic is a decision function CI can run —
    /// the meter itself needs a window and cannot (see the harness README, "the CI boundary").
    /// </summary>
    public static FrameCostSummary? Of(IReadOnlyList<FrameCost> costs, IReadOnlyList<int> roundOrdinals, int rounds)
    {
        var roundsByOrdinal = roundOrdinals.GroupBy(o => o).ToDictionary(g => g.Key, g => g.Count());
        var over = costs.Where(c => roundsByOrdinal.GetValueOrDefault(c.Ordinal) >= rounds).ToList();
        if (over.Count == 0)
        {
            return null;
        }

        // Frames that ran no layout at all are not the comparison. They are cheap because nothing happened in
        // them, and averaging them in flatters the contrast: the baseline has to be a frame that did the work.
        var under = costs
            .Where(c => roundsByOrdinal.GetValueOrDefault(c.Ordinal) > 0 && roundsByOrdinal[c.Ordinal] < rounds)
            .ToList();
        var roundsOver = over.Sum(c => (long)roundsByOrdinal[c.Ordinal]);
        var roundsUnder = under.Sum(c => (long)roundsByOrdinal.GetValueOrDefault(c.Ordinal));
        return new FrameCostSummary(
            over.Count,
            costs.Count,
            over.Min(c => c.Milliseconds),
            over.Max(c => c.Milliseconds),
            over.Sum(c => c.Milliseconds),
            under.Count == 0 ? null : under.Average(c => c.Milliseconds),
            over.Sum(c => c.AllocatedBytes),
            roundsOver == 0 ? 0 : over.Sum(c => c.AllocatedBytes) / roundsOver,
            roundsUnder == 0 ? null : under.Sum(c => c.AllocatedBytes) / roundsUnder);
    }

    public string Line(string label) =>
        $"{label}: {Frames} of {FramesTotal} frames, {ShortestMs:F0}–{LongestMs:F0} ms each ("
        + $"{(AverageOtherFrameMs is { } other ? $"other frames average {other:F1} ms" : "no other frames to compare with")}"
        + $"), {TotalMs:F0} ms in total, {AllocatedBytes:N0} bytes allocated on the UI thread, "
        + $"{AllocatedBytesPerRound:N0} bytes per round against "
        + $"{(OtherAllocatedBytesPerRound is { } baseline ? $"{baseline:N0}" : "n/a")} in the other frames";
}

/// <summary>
/// The longest gap between dispatcher ticks. Also the negative control for a parked render clock: during
/// that fault the dispatcher keeps ticking, and that is what tells it apart from a busy UI thread.
/// </summary>
public sealed class DispatcherGapMeter
{
    private DispatcherTimer? _timer;
    private long _lastTick;

    public double LongestGapMs { get; private set; }

    public int Ticks { get; private set; }

    public void Start(TimeSpan interval)
    {
        _lastTick = Stopwatch.GetTimestamp();
        _timer = new DispatcherTimer(DispatcherPriority.Default) { Interval = interval };
        _timer.Tick += (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            LongestGapMs = Math.Max(LongestGapMs, Stopwatch.GetElapsedTime(_lastTick, now).TotalMilliseconds);
            _lastTick = now;
            Ticks++;
        };
        _timer.Start();
    }

    public void Stop() => _timer?.Stop();

    public string Line(string label) => $"{label}: {Ticks} ticks, longest gap {LongestGapMs:F1} ms";
}

/// <summary>
/// E6: proof that there is a render clock to have an opinion about. Headless returns no compositor, so the
/// probe never starts and `rclock` stays n/a in every phase, healthy ones included — that is the shape the
/// evidence under PR #934 had, and it is why a claim about stalls may not rest on a headless run.
/// </summary>
public sealed class RenderClockWitness
{
    private int _returned;
    private long _startedAt;
    private int _inFlight;

    public bool CompositorPresent { get; private set; }

    /// <summary>True once a commit has actually come back. Until then this harness knows nothing about stalls.</summary>
    public bool EverReturned => Volatile.Read(ref _returned) > 0;

    /// <summary>
    /// How long the outstanding commit has been outstanding, or null when none is. This is the value the app's
    /// own <c>RenderClockHeartbeat</c> decides on, so a scenario can hand it the real one instead of a story.
    /// </summary>
    public TimeSpan? OutstandingFor =>
        Volatile.Read(ref _inFlight) == 0 ? null : Stopwatch.GetElapsedTime(Volatile.Read(ref _startedAt));

    /// <summary>
    /// Asks the compositor for a commit and notes whether it comes back. One probe at a time, mirroring the
    /// app: a second in flight would make "outstanding for" mean two different things at once.
    /// </summary>
    public void Probe()
    {
        if (Compositor.TryGetDefaultCompositor() is not { } compositor)
        {
            CompositorPresent = false;
            return;
        }

        CompositorPresent = true;
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            return;
        }

        Volatile.Write(ref _startedAt, Stopwatch.GetTimestamp());
        compositor.RequestCommitAsync().ContinueWith(
            _ =>
            {
                Interlocked.Increment(ref _returned);
                Volatile.Write(ref _inFlight, 0);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Why this run may not speak about the render clock, for the gate's failure line.</summary>
    public string Failure => CompositorPresent
        ? "the compositor never returned a commit, so nothing here distinguishes a stall from a probe that never ran"
        : "there is no compositor (headless), so the render clock was never observed at all";
}
