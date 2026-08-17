using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Services;

// AC-718: one background thread for both jobs below — a UI freeze stops the existing DispatcherTimer sampler
// (CockpitView.axaml.cs), and both the diagnostics snapshot and the heartbeat need to keep running through it.
public sealed class DiagnosticsBackgroundService : ISingletonService, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(10);

    // AC-882: how often to ask the compositor for a commit and time how long it takes to be processed. The probe
    // costs one extra frame per interval on an otherwise idle app — the price of knowing the render clock still
    // wakes, which nothing else in the process can tell us.
    private static readonly TimeSpan RenderClockProbeInterval = TimeSpan.FromSeconds(10);
#if DEBUG
    // Opt-in only: the leak-tracker's periodic report forces a full blocking gen2 GC, so a normal debug run must
    // not pay that stutter. Set COCKPIT_LEAKSIM=1 to arm the leak diagnostics (and the on-demand leak-sim trigger).
    internal static readonly bool LeakDiagnosticsEnabled =
        Environment.GetEnvironmentVariable("COCKPIT_LEAKSIM") is { Length: > 0 };
#endif

    private readonly ILogger<DiagnosticsBackgroundService> _logger;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private Thread? _thread;
    private volatile bool _stopping;
    private volatile bool _snapshotsEnabled;

    // Written on the UI thread (dispatcher post below), read on the background thread — Interlocked guards the
    // cross-thread 64-bit access. -1 means "no pong yet", which _Run uses to avoid arming the heartbeat before
    // the dispatcher loop is even pumping.
    private long _lastPongTicks = -1;

    // Set on the background thread before the probe is posted, cleared by the commit's continuation on whichever
    // thread completes it. While true no second probe is posted, so an outstanding probe is the stall itself.
    private volatile bool _probeInFlight;

    // Stamped on the UI thread when the commit is actually requested, not when the probe was posted: a hung UI
    // thread then delays the probe rather than reading as a stalled render clock (that is _LogHang's job).
    private long _probeStartedTicks;

    // Last completed probe's round trip; -1 until one completes. Reported on the snapshot line as rclock=.
    private long _probeRoundTripTicks = -1;

    public DiagnosticsBackgroundService(ILogger<DiagnosticsBackgroundService> logger)
    {
        _logger = logger;
    }

    // Flips the periodic-snapshot half on or off live — called by CockpitViewModel from the Options checkbox and
    // at startup load. The heartbeat half is not gated by this; it always runs.
    public void SetSnapshotsEnabled(bool enabled) => _snapshotsEnabled = enabled;

    // AC-718: not an IHostedService — those start via Program.StartHostedServices, before Avalonia's Setup()
    // binds Dispatcher.UIThread, and touching it that early crashed the app on launch (races Setup() for
    // dispatcher ownership). Called from App.axaml.cs instead, once the framework init has bound it.
    public void Start()
    {
        _thread = new Thread(_Run) { IsBackground = true, Name = "cockpit-diagnostics" };
        _thread.Start();
    }

    public void Dispose() => _stopping = true;

    private void _Run()
    {
        var cpu = new _CpuSampler();
        var nextSnapshotAt = TimeSpan.Zero;
        var warned = false;
        var hangStartedAt = TimeSpan.Zero;
        var renderClockWarned = false;
        var renderStallStartedAt = TimeSpan.Zero;
        var nextProbeAt = TimeSpan.Zero;
#if DEBUG
        var nextLeakAt = TimeSpan.Zero;
#endif

        while (!_stopping)
        {
            try
            {
                var now = _clock.Elapsed;

                // Background priority: work already queued ahead of it counts against the pong too, so a
                // dispatcher merely drowning in queued work reads as a hang the same as one truly stuck — which
                // is the complaint this exists to catch.
                Dispatcher.UIThread.Post(() => Interlocked.Exchange(ref _lastPongTicks, _clock.Elapsed.Ticks), DispatcherPriority.Background);

                var lastPongTicks = Interlocked.Read(ref _lastPongTicks);
                if (lastPongTicks >= 0)
                {
                    var sinceLastPong = TimeSpan.FromTicks(_clock.Elapsed.Ticks - lastPongTicks);
                    var decision = UiThreadHeartbeat.Decide(sinceLastPong, warned);

                    if (decision.Warn)
                    {
                        hangStartedAt = now;
                        _LogHang(sinceLastPong, cpu);
                    }
                    else if (decision.Recovered)
                    {
                        _LogRecovery(now - hangStartedAt);
                    }

                    warned = decision.Warned;
                }

                if (!_probeInFlight && now >= nextProbeAt)
                {
                    nextProbeAt = now + RenderClockProbeInterval;
                    _probeInFlight = true;
                    Interlocked.Exchange(ref _probeStartedTicks, 0);
                    Dispatcher.UIThread.Post(_StartRenderClockProbe, DispatcherPriority.Background);
                }

                var renderDecision = RenderClockHeartbeat.Decide(_ProbeInFlightFor(now), renderClockWarned);
                if (renderDecision.Stalled)
                {
                    renderStallStartedAt = now;
                    _logger.LogWarning(
                        "renderclock stalled since={Since:0.0}s — a forced compositor commit has not been processed",
                        RenderClockHeartbeat.StallAfter.TotalSeconds);
                }
                else if (renderDecision.Resumed)
                {
                    _logger.LogWarning("renderclock resumed after={Duration:0.0}s", (now - renderStallStartedAt).TotalSeconds);
                }

                renderClockWarned = renderDecision.Warned;

                if (_snapshotsEnabled && now >= nextSnapshotAt)
                {
                    _WriteSnapshot(cpu, renderClockWarned);
                    nextSnapshotAt = now + SnapshotInterval;
                }
#if DEBUG
                if (LeakDiagnosticsEnabled && now >= nextLeakAt)
                {
                    // 60s, not the 10s snapshot cadence: ReportAfterGc forces a full blocking gen2 GC, which on a
                    // multi-hundred-MB dev heap is a visible stutter — too costly to do every 10 seconds.
                    _logger.LogInformation(Cockpit.App.Diagnostics.LeakTracker.ReportAfterGc());
                    nextLeakAt = now + TimeSpan.FromSeconds(60);
                }
#endif
            }
            catch (Exception exception)
            {
                // A diagnostics thread must never be the reason the cockpit falls over — same discipline as
                // ScheduledResumeCoordinator._OnTick. The next tick, a second later, tries again.
                _logger.LogWarning(exception, "A diagnostics tick failed; the next one will try again.");
            }

            Thread.Sleep(TickInterval);
        }
    }

    // Null while nothing is outstanding, and while a posted probe is still waiting for the UI thread to run it.
    private TimeSpan? _ProbeInFlightFor(TimeSpan now)
    {
        if (!_probeInFlight)
        {
            return null;
        }

        var startedAt = Interlocked.Read(ref _probeStartedTicks);
        return startedAt > 0 ? TimeSpan.FromTicks(now.Ticks - startedAt) : null;
    }

    // AC-882: the one thing that proves the render clock can still be woken. Avalonia's commit chain wakes a
    // parked clock itself (ServerCompositor.EnqueueBatch → IRenderLoop.Wakeup), so a commit that never reports
    // Processed means the platform timer stopped delivering ticks and Wakeup can no longer reach it.
    private void _StartRenderClockProbe()
    {
        try
        {
            if (Compositor.TryGetDefaultCompositor() is not { } compositor)
            {
                // No platform yet, or already torn down. Not a stall — try again on the next interval.
                _probeInFlight = false;
                return;
            }

            var startedAt = _clock.Elapsed;
            Interlocked.Exchange(ref _probeStartedTicks, startedAt.Ticks);

            compositor.RequestCommitAsync().ContinueWith(
                _ =>
                {
                    Interlocked.Exchange(ref _probeRoundTripTicks, (_clock.Elapsed - startedAt).Ticks);
                    _probeInFlight = false;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception exception)
        {
            // Leaving _probeInFlight set would read as a permanent stall from here on — the opposite of a diagnostic.
            _probeInFlight = false;
            _logger.LogWarning(exception, "A render-clock probe could not be started; the next one will try again.");
        }
    }

    private void _WriteSnapshot(_CpuSampler cpu, bool renderClockStalled)
    {
        var snapshot = DiagnosticsCollector.SelfReadSnapshot();
        var memory = snapshot.Memory;
        var heap = snapshot.ManagedHeap;

        using var process = Process.GetCurrentProcess();

        _logger.LogInformation(
            "diag rss={Rss} peak={Peak} virt={Virt} priv={Priv} heap={Heap} live={Live} alloc={Alloc} " +
            "gc={Gen0}/{Gen1}/{Gen2} gcpause={GcPause:0.0}% handles={Handles} threads={Threads} tp={Pending}/{ThreadPoolCount} " +
            "cpu={Cpu:0.0}% rclock={RenderClock}",
            _Compact(memory.ResidentBytes),
            _Compact(memory.PeakResidentBytes),
            _Compact(memory.VirtualBytes),
            _Compact(memory.PrivateBytes),
            _Compact(heap.HeapSizeBytes),
            _Compact(heap.LiveManagedBytes),
            _Compact(heap.TotalAllocatedBytes),
            heap.Gen0Collections,
            heap.Gen1Collections,
            heap.Gen2Collections,
            GC.GetGCMemoryInfo().PauseTimePercentage,
            _HandleCountText(process),
            process.Threads.Count,
            ThreadPool.PendingWorkItemCount,
            ThreadPool.ThreadCount,
            cpu.PercentSinceLastCall(),
            _RenderClockText(renderClockStalled));
    }

    // AC-882: the field that makes a future macOS reproduction decisive instead of inferred — how long the last
    // forced commit took to be processed, or that one is outstanding past the stall threshold.
    private string _RenderClockText(bool stalled)
    {
        if (stalled)
        {
            return "stalled";
        }

        var ticks = Interlocked.Read(ref _probeRoundTripTicks);
        return ticks < 0
            ? "n/a"
            : TimeSpan.FromTicks(ticks).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + "ms";
    }

    // Not elsewhere in Cockpit.Core.Diagnostics because nothing else needs it: the Debug tab's report never
    // shows handle count, and this is the only caller.
    private void _LogHang(TimeSpan sinceLastPong, _CpuSampler cpu)
    {
        using var process = Process.GetCurrentProcess();

        // The distinction this buys without a callstack (which no supported .NET API can give us for another
        // thread, macOS included): tolling versus blocked. A high CPU delta plus queued threadpool work says the
        // process is busy somewhere; near-zero CPU says something is genuinely stuck waiting.
        var threadStates = process.Threads
            .Cast<ProcessThread>()
            .GroupBy(thread => thread.ThreadState)
            .OrderBy(group => group.Key.ToString(), StringComparer.Ordinal)
            .Select(group => $"{group.Key}={group.Count()}");

        _logger.LogWarning(
            "uifreeze hang since={Since:0.0}s cpu={Cpu:0.0}% tpPending={Pending} tpThreads={ThreadPoolCount} gcpause={GcPause:0.0}% threadStates={States}",
            sinceLastPong.TotalSeconds,
            cpu.PercentSinceLastCall(),
            ThreadPool.PendingWorkItemCount,
            ThreadPool.ThreadCount,
            GC.GetGCMemoryInfo().PauseTimePercentage,
            string.Join(' ', threadStates));
    }

    private void _LogRecovery(TimeSpan hungFor) =>
        _logger.LogWarning("uifreeze recovered after={Duration:0.0}s", hungFor.TotalSeconds);

    private static string _Compact(long bytes) => ByteSize.Human(bytes).Replace(" ", string.Empty);

    // AC-718: macOS silently returns 0 from Process.HandleCount (ProcessManager.OSX.cs's EnsureHandleCountPopulated
    // has no body there) rather than throwing — same trap ProcessMemoryInfo.cs already works around for peak
    // resident. No native replacement here, so n/a rather than a misleading 0.
    private static string _HandleCountText(Process process) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? "n/a"
            : process.HandleCount.ToString(CultureInfo.InvariantCulture);

    // Same idiom as plugins-dev/Cockpit.Plugin.SystemMonitor/SystemUsage.CpuPercent (Environment.CpuUsage over
    // Process.TotalProcessorTime — no native Process handle held per reading), duplicated rather than shared
    // because plugins-dev sits outside Cockpit.slnx and nothing in the host is meant to reference it.
    private sealed class _CpuSampler
    {
        private TimeSpan _lastCpuTime = TimeSpan.Zero;
        private DateTimeOffset _lastSampledAt = DateTimeOffset.MinValue;

        public double PercentSinceLastCall()
        {
            var now = DateTimeOffset.UtcNow;
            var cpu = Environment.CpuUsage.TotalTime;

            if (_lastSampledAt == DateTimeOffset.MinValue)
            {
                (_lastCpuTime, _lastSampledAt) = (cpu, now);
                return 0;
            }

            var elapsedMs = (now - _lastSampledAt).TotalMilliseconds;
            var usedMs = (cpu - _lastCpuTime).TotalMilliseconds;
            (_lastCpuTime, _lastSampledAt) = (cpu, now);

            return elapsedMs <= 0 ? 0 : Math.Clamp(usedMs / (elapsedMs * Environment.ProcessorCount) * 100, 0, 100);
        }
    }
}
