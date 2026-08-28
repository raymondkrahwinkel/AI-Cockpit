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
    private long _lastFrame;
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
            Ordinal++;
            var now = Stopwatch.GetTimestamp();
            if (_lastFrame != 0)
            {
                _intervals.Add(Stopwatch.GetElapsedTime(_lastFrame, now).TotalMilliseconds);
            }

            _lastFrame = now;
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
