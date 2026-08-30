using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Cockpit.App.Diagnostics;
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

    // AC-1256: how many times one freeze episode is asked what is still in layout, and how far apart. Three is
    // what tells a stuck subtree from one walking the tree; more would only lengthen the log.
    private const int DirtySamplesPerEpisode = 3;

    private static readonly TimeSpan DirtySampleInterval = TimeSpan.FromSeconds(10);

    // AC-1114: how often to check that the AppImage mount this process runs from still serves.
    private static readonly TimeSpan AppImageMountProbeInterval = TimeSpan.FromSeconds(10);

    // Two in a row, so a one-off failure (a momentary descriptor shortage, say) cannot take the cockpit down.
    // A mount that has lost its daemon never recovers, so waiting for the second probe costs nothing.
    private const int FailedMountProbesBeforeExit = 2;

    // Distinct from any exit the app makes itself, so this shows up as its own thing in the journal.
    private const int AppImageMountLostExitCode = 70;
#if DEBUG
    // Opt-in only: the leak-tracker's periodic report forces a full blocking gen2 GC, so a normal debug run must
    // not pay that stutter. Set COCKPIT_LEAKSIM=1 to arm the leak diagnostics (and the on-demand leak-sim trigger).
    internal static readonly bool LeakDiagnosticsEnabled =
        Environment.GetEnvironmentVariable("COCKPIT_LEAKSIM") is { Length: > 0 };

    private static readonly string? MeasurementRoot =
        Environment.GetEnvironmentVariable("COCKPIT_MEASUREMENT_ROOT");
#endif

    private readonly ILogger<DiagnosticsBackgroundService> _logger;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // AC-1114: null unless this process runs from an AppImage whose mount answered at startup. Set once in
    // Start, before the loop that reads it exists.
    private string? _appImageProbePath;

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

    // Stamped on the UI thread when the commit is actually requested, not when the probe was posted: this is the
    // "started, no render answer" half of AC-1196's two states, and the only one the render clock owns outright.
    private long _probeStartedTicks;

    // AC-1196 T4: stamped on this thread, at the post. A probe that never gets picked up has to be measurable
    // without help from the thread that would pick it up — which is exactly the thread that is in trouble.
    private long _probeQueuedTicks;

    // AC-1196: when work above the priorities a runaway layout loop reposts at last ran. This is the whole
    // discriminator: a starved dispatcher still answers here, a blocked one answers nothing. -1 means never.
    private long _highPriorityPongTicks = -1;

    // Last completed probe's round trip; -1 until one completes. Reported on the snapshot line as rclock=.
    private long _probeRoundTripTicks = -1;

    // AC-883: set on this thread, read by views on the UI thread — volatile so a subscriber that reads it on
    // subscribe rather than waiting for the next edge sees the current answer.
    private volatile bool _renderersShouldPause;

    // AC-1125 C: injectable so a test can force the forced-full-GC hang sample's ceiling branch without growing
    // a real multi-gigabyte heap. Defaults to the real heap size.
    private readonly Func<long> _heapBytesProbe;

    // AC-1125 D: read at snapshot time, not pushed on every session/layout change — set once from App.axaml.cs
    // once both this service and CockpitViewModel exist (CockpitViewModel already depends on this service, so
    // the dependency cannot run the other way). Null (falls back to "n/a") before that wiring runs, e.g. in tests.
    private Func<(int OpenSessions, string LayoutStand)>? _sessionContext;

    // AC-1256: the windows the starvation probe reads the layout tree from. Same shape as Program's own
    // `_OpenWindows`, kept here rather than plumbed in so production needs no wiring for a diagnostic.
    private Func<IReadOnlyList<Visual>>? _layoutRoots;

    // AC-1196: the budget both alarms below are judged against. Injectable for the same reason heapBytesProbe is —
    // a test would otherwise have to hold a thread hostage for a real quarter-minute per case, three times over.
    private readonly TimeSpan _alarmAfter;

    public DiagnosticsBackgroundService(
        ILogger<DiagnosticsBackgroundService> logger, Func<long>? heapBytesProbe = null, TimeSpan? alarmAfter = null)
    {
        _logger = logger;
        _heapBytesProbe = heapBytesProbe ?? (() => GC.GetGCMemoryInfo().HeapSizeBytes);
        _alarmAfter = alarmAfter ?? RenderClockHeartbeat.StallAfter;
    }

    // Wired once from App.axaml.cs: the three fields the diag line cannot compute on its own (AC-1125 D).
    public void SetSessionContext(Func<(int OpenSessions, string LayoutStand)> provider) => _sessionContext = provider;

    // AC-1256: test seam only. The default below is what production uses, so nothing has to wire this.
    internal void SetLayoutRoots(Func<IReadOnlyList<Visual>> provider) => _layoutRoots = provider;

    // AC-883: raised on the UI thread when the render clock starts or stops being able to process commits. Panes
    // subscribe to suspend their transcripts; nothing else in the process can tell them the clock is gone.
    public event EventHandler<bool>? RenderersShouldPauseChanged;

    // The current answer, for a subscriber that attaches between two edges.
    public bool RenderersShouldPause => _renderersShouldPause;

    // Flips the periodic-snapshot half on or off live — called by CockpitViewModel from the Options checkbox and
    // at startup load. The heartbeat half is not gated by this; it always runs.
    public void SetSnapshotsEnabled(bool enabled) => _snapshotsEnabled = enabled;

    // Internal so a view test can drive the edge without a compositor that really stalls. Posts rather than raising
    // inline: the caller is this background thread and every subscriber touches visuals.
    internal void SetRenderersShouldPause(bool shouldPause)
    {
        if (shouldPause == _renderersShouldPause)
        {
            return;
        }

        _renderersShouldPause = shouldPause;
        Dispatcher.UIThread.Post(() => RenderersShouldPauseChanged?.Invoke(this, shouldPause));
    }

    // AC-718: not an IHostedService — those start via Program.StartHostedServices, before Avalonia's Setup()
    // binds Dispatcher.UIThread, and touching it that early crashed the app on launch (races Setup() for
    // dispatcher ownership). Called from App.axaml.cs instead, once the framework init has bound it.
    public void Start()
    {
        _StartAppImageMountWatch();

        _thread = new Thread(_Run) { IsBackground = true, Name = "cockpit-diagnostics" };
        _thread.Start();
    }

    // AC-1114: probed once, here, so the loop only ever watches a mount that demonstrably served at startup.
    // Without that, an APPDIR whose probe was never readable would read as a mount that died twenty seconds
    // in — the same unexplained shutdown this check exists to prevent, only with an untrue reason.
    private void _StartAppImageMountWatch()
    {
        var appDir = Environment.GetEnvironmentVariable("APPDIR");
        _appImageProbePath = AppImageMount.WatchablePathFrom(appDir);

        if (_appImageProbePath is null && !string.IsNullOrWhiteSpace(appDir))
        {
            _logger.LogInformation(
                "appimage mount watch off — nothing readable to watch under APPDIR={AppDir} at startup, so a " +
                "mount that goes away later will not be reported.",
                appDir);
        }
    }

    public void Dispose() => _stopping = true;

    private void _Run()
    {
        // AC-1125 A: a silent log cannot be told apart from a dead thread — this line settles that question for
        // every run, past or present, without needing a reproduction.
        _logger.LogInformation(
            "diagnostics thread running snapshots={Snapshots} warnAfter={WarnAfter:0}s stallAfter={StallAfter:0}s",
            _snapshotsEnabled, UiThreadHeartbeat.WarnAfter.TotalSeconds, _alarmAfter.TotalSeconds);

        // AC-1125 F: one sampler per caller, not one shared between them — PercentSinceLastCall() resets its own
        // baseline on every call, so two callers sharing one instance stole each other's measurement window.
        var hangCpu = new CpuSampler();
        var snapshotCpu = new CpuSampler();
        var nextSnapshotAt = TimeSpan.Zero;
        var warned = false;
        var hangStartedAt = TimeSpan.Zero;
        var renderClockWarned = false;
        var renderStallStartedAt = TimeSpan.Zero;
        var dispatchWarned = false;
        var dispatchStarvedAt = TimeSpan.Zero;
        var nextProbeAt = TimeSpan.Zero;
        var nextDirtySampleAt = TimeSpan.Zero;
        var dirtySamplesTaken = 0;
        var nextMountProbeAt = TimeSpan.Zero;
        var failedMountProbes = 0;
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

                // AC-1196: a measurement, not work — one Interlocked.Exchange per tick. AC-1138/AC-1204's "do not
                // lift work to Render or Send" still holds; this is posted there precisely because Send outranks
                // the Loaded(1)/Render(4) a layout loop reposts at, and surviving it is what says starved, not stuck.
                Dispatcher.UIThread.Post(() => Interlocked.Exchange(ref _highPriorityPongTicks, _clock.Elapsed.Ticks), DispatcherPriority.Send);

                var sinceHighPriorityPong = _SinceHighPriorityPong(now);
                var lastPongTicks = Interlocked.Read(ref _lastPongTicks);
                if (lastPongTicks >= 0)
                {
                    var sinceLastPong = TimeSpan.FromTicks(_clock.Elapsed.Ticks - lastPongTicks);
                    var decision = UiThreadHeartbeat.Decide(sinceLastPong, warned);

                    if (decision.Warn)
                    {
                        hangStartedAt = now;
                        _LogHang(sinceLastPong, hangCpu, sinceHighPriorityPong);
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
                    Interlocked.Exchange(ref _probeStartedTicks, 0);
                    Interlocked.Exchange(ref _probeQueuedTicks, now.Ticks);
                    _probeInFlight = true;
                    Dispatcher.UIThread.Post(_StartRenderClockProbe, DispatcherPriority.Background);
                }

                // AC-1196 T5: the two states, both read from here rather than from the thread under suspicion.
                var probeStartedFor = _ProbeStartedFor(now);
                var probePendingFor = _ProbePendingFor(now);

                // AC-883/AC-1196: fed the started half only. Pausing panes answers a clock that stopped after the
                // commit was requested; a thread that never picked the probe up cannot render either way, and the
                // event that would carry the pause is queued behind that very thread. So the macOS gate stays.
                SetRenderersShouldPause(
                    RenderClockHeartbeat.ShouldPauseRenderers(probeStartedFor, OperatingSystem.IsMacOS()));

                var dispatchDecision = UiDispatchHeartbeat.Decide(
                    probePendingFor, sinceHighPriorityPong, dispatchWarned, _alarmAfter);
                if (dispatchDecision.Starved)
                {
                    dispatchStarvedAt = now;
                    _logger.LogWarning(
                        "uidispatch starved pending={Pending:0.0}s hipri={HiPri:0.0}s — the UI thread is running "
                        + "and answering above its layout priority, but nothing at Background is being reached. "
                        + "This is not a renderclock stall: the clock is ticking.",
                        probePendingFor!.Value.TotalSeconds,
                        sinceHighPriorityPong!.Value.TotalSeconds);
                }
                else if (dispatchDecision.Recovered)
                {
                    _logger.LogWarning("uidispatch recovered after={Duration:0.0}s", (now - dispatchStarvedAt).TotalSeconds);
                }

                dispatchWarned = dispatchDecision.Warned;

                // AC-1256: whichever alarm stands, not the starvation edge alone — on the reported freeze uifreeze
                // fired at 5.1s and starvation only at 15.4s, so the later of the two reads a quarter-minute late.
                // Repeated, because one reading cannot tell a stuck subtree from one walking through the tree.
                if (warned || dispatchWarned)
                {
                    if (now >= nextDirtySampleAt && dirtySamplesTaken < DirtySamplesPerEpisode)
                    {
                        nextDirtySampleAt = now + DirtySampleInterval;
                        _AskWhatIsStillInLayout(++dirtySamplesTaken, now);
                    }
                }
                else
                {
                    dirtySamplesTaken = 0;
                    nextDirtySampleAt = TimeSpan.Zero;
                }

                var renderDecision = RenderClockHeartbeat.Decide(
                    UiDispatchHeartbeat.RenderClockOutstandingFor(
                        probeStartedFor, probePendingFor, sinceHighPriorityPong, _alarmAfter),
                    renderClockWarned,
                    _alarmAfter);
                if (renderDecision.Stalled)
                {
                    renderStallStartedAt = now;
                    _logger.LogWarning(
                        "renderclock stalled since={Since:0.0}s — a forced compositor commit has not been processed",
                        _alarmAfter.TotalSeconds);
                }
                else if (renderDecision.Resumed)
                {
                    _logger.LogWarning("renderclock resumed after={Duration:0.0}s", (now - renderStallStartedAt).TotalSeconds);
                }

                renderClockWarned = renderDecision.Warned;

#if DEBUG
                if (MeasurementRoot is { } measurementRoot)
                {
                    _TryWriteMeasurementHostReady(measurementRoot);
                }
#endif

                if (_appImageProbePath is not null && now >= nextMountProbeAt)
                {
                    nextMountProbeAt = now + AppImageMountProbeInterval;

                    failedMountProbes = AppImageMount.CanStillServe(_appImageProbePath) ? 0 : failedMountProbes + 1;
                    if (failedMountProbes >= FailedMountProbesBeforeExit)
                    {
                        _ExitOnLostAppImageMount();
                    }
                }

                if (_snapshotsEnabled && now >= nextSnapshotAt)
                {
                    WriteSnapshot(snapshotCpu, renderClockWarned);
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

#if DEBUG
    private void _TryWriteMeasurementHostReady(string root)
    {
        var path = Path.Combine(root, "measurement-host.ready.json");
        if (File.Exists(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId,
                stateRoot = Cockpit.Core.Configuration.CockpitBuild.StateRoot
            }));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Measurement host-ready probe failed.");
        }
    }
#endif

    // Started, and no render answer yet — null while nothing is outstanding and while a posted probe is still
    // waiting for its turn. That second case used to be all this reported, and it is why AC-1196 saw nothing.
    private TimeSpan? _ProbeStartedFor(TimeSpan now)
    {
        if (!_probeInFlight)
        {
            return null;
        }

        var startedAt = Interlocked.Read(ref _probeStartedTicks);
        return startedAt > 0 ? TimeSpan.FromTicks(now.Ticks - startedAt) : null;
    }

    // Posted, never run — measured from the stamp this thread took at the post, so nothing here needs the UI
    // thread to be well enough to answer. The other half of T5, and the half neither alarm could see before.
    private TimeSpan? _ProbePendingFor(TimeSpan now)
    {
        if (!_probeInFlight || Interlocked.Read(ref _probeStartedTicks) > 0)
        {
            return null;
        }

        return TimeSpan.FromTicks(now.Ticks - Interlocked.Read(ref _probeQueuedTicks));
    }

    private TimeSpan? _SinceHighPriorityPong(TimeSpan now)
    {
        var pongTicks = Interlocked.Read(ref _highPriorityPongTicks);
        return pongTicks < 0 ? null : TimeSpan.FromTicks(now.Ticks - pongTicks);
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

    // AC-1256: the alarms say a pass never finishes, never whose. AC-1236 already reads that off the tree for a
    // cut-off pass; a stuck or starved one leaves the same trace but never throws, so nothing went to look. Send
    // outranks the Loaded/Render such a loop reposts at (`hipri` is that proof), so a busy thread still runs it.
    private void _AskWhatIsStillInLayout(int sample, TimeSpan requestedAt)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                try
                {
                    var elements = LayoutLoopReport.Describe(_layoutRoots?.Invoke() ?? _OpenWindows());

                    // `queued` is how long this reading waited for the thread: a sample taken long after it was
                    // asked for is not a reading of the moment it was asked about, and has to say so itself.
                    _logger.LogWarning(
                        "uilayout dirty sample={Sample}/{Total} queued={Queued:0.0}s {Count} element(s) still in layout: {Elements}",
                        sample,
                        DirtySamplesPerEpisode,
                        (_clock.Elapsed - requestedAt).TotalSeconds,
                        elements.Count,
                        elements.Count == 0 ? "(none — the loop is not a layout pass)" : string.Join(" | ", elements));
                }
                catch (Exception exception)
                {
                    // A diagnostic that throws on the thread it is diagnosing would be the second freeze.
                    _logger.LogWarning("Could not read the layout tree during a freeze: {Failure}", exception);
                }
            },
            DispatcherPriority.Send);
    }

    private static IReadOnlyList<Visual> _OpenWindows() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? [.. desktop.Windows]
            : [];

    // Internal (a test seam, same idiom as AdaptiveGcCompactor.CheckOnce): one snapshot line, driven directly
    // instead of through the thread's 10s cadence.
    internal void WriteSnapshot(CpuSampler cpu, bool renderClockStalled)
    {
        var snapshot = DiagnosticsCollector.SelfReadSnapshot();
        var memory = snapshot.Memory;
        var heap = snapshot.ManagedHeap;

        using var process = Process.GetCurrentProcess();

        // AC-1125 D: known right where the line is built, so nothing has to be pushed here in advance.
        var (openSessions, layoutStand) = _sessionContext?.Invoke() ?? (0, "n/a");

        _logger.LogInformation(
            "diag rss={Rss} peak={Peak} virt={Virt} priv={Priv} heap={Heap} managed={Managed} alloc={Alloc} " +
            "gc={Gen0}/{Gen1}/{Gen2} gcpause={GcPause:0.0}% handles={Handles} threads={Threads} tp={Pending}/{ThreadPoolCount} " +
            "cpu={Cpu} rclock={RenderClock} uptime={Uptime} sessions={Sessions} layout={Layout}",
            _Compact(memory.ResidentBytes),
            _Compact(memory.PeakResidentBytes),
            _Compact(memory.VirtualBytes),
            PrivText(memory.PrivateBytes),
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
            CpuText(cpu.PercentSinceLastCall()),
            _RenderClockText(renderClockStalled),
            _UptimeText(_clock.Elapsed),
            openSessions,
            layoutStand);
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
    private void _LogHang(TimeSpan sinceLastPong, CpuSampler cpu, TimeSpan? sinceHighPriorityPong)
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

        // AC-1196: hipri= is what stops this line being read as "the thread is stuck" when it is not. A fresh
        // reading means the thread is pumping and only its lower priorities are starved — a different fault, with
        // its own uidispatch line, and the misdiagnosis that sent the last round after the wrong thing.
        _logger.LogWarning(
            "uifreeze hang since={Since:0.0}s hipri={HiPri} cpu={Cpu} retention={Retention} tpPending={Pending} tpThreads={ThreadPoolCount} gcpause={GcPause:0.0}% threadStates={States}",
            sinceLastPong.TotalSeconds,
            AgoText(sinceHighPriorityPong),
            CpuText(cpu.PercentSinceLastCall()),
            SampleHangRetention(),
            ThreadPool.PendingWorkItemCount,
            ThreadPool.ThreadCount,
            GC.GetGCMemoryInfo().PauseTimePercentage,
            string.Join(' ', threadStates));
    }

    // AC-1125 C: a heap ceiling above which forceFullCollection:true is skipped — extrapolated with margin from
    // the one measurement we have (17.1s on a 10.2GB heap, AC-1184), not itself measured. Internal so a test can
    // drive both branches without growing a real multi-gigabyte heap.
    internal const long HangGcSampleCeilingBytes = 1L * 1024 * 1024 * 1024;

    internal string SampleHangRetention()
    {
        var heapBytes = _heapBytesProbe();
        if (heapBytes > HangGcSampleCeilingBytes)
        {
            _logger.LogWarning(
                "uifreeze GC retention sample skipped — heap ({Heap}) is over the {Ceiling} ceiling; a forced " +
                "full collection there costs seconds and would trip this same alarm.",
                ByteSize.Human(heapBytes),
                ByteSize.Human(HangGcSampleCeilingBytes));
            return "skipped";
        }

        // Same meter on both sides of the arrow (AC-1125 review): HeapSizeBytes above is only the ceiling gate —
        // committed heap and GetTotalMemory's live-bytes estimate are different quantities, and printing one as
        // "before" the other as "after" would misreport their gap as reclaimed memory.
        var before = GC.GetTotalMemory(forceFullCollection: false);
        var after = GC.GetTotalMemory(forceFullCollection: true);
        return $"{_Compact(before)}->{_Compact(after)}";
    }

    private void _LogRecovery(TimeSpan hungFor) =>
        _logger.LogWarning("uifreeze recovered after={Duration:0.0}s", hungFor.TotalSeconds);

    // AC-1114: there is nothing to recover here. The mount is gone, so every code page not already resident
    // faults the moment it is needed — the process is going to die, the only open question is whether it does
    // so with a 400 MB coredump and no explanation. Leaving on our own terms is the whole win.
    private void _ExitOnLostAppImageMount()
    {
        const string message =
            "appimage mount lost — the mount this cockpit runs its own code from no longer serves reads. " +
            "Shutting down before an unreadable code page takes the process down with SIGBUS. Restart the cockpit.";

        _logger.LogCritical("{Message} probe={ProbePath}", message, _appImageProbePath);

        // AddConsole() hands its writes to a background queue that a plain Environment.Exit never drains, and
        // this line is the entire point of the check — so it also goes straight out, unbuffered.
        Console.Error.WriteLine($"{message} probe={_appImageProbePath}");
        Console.Error.Flush();

        Environment.Exit(AppImageMountLostExitCode);
    }

    private static string _Compact(long bytes) => ByteSize.Human(bytes).Replace(" ", string.Empty);

    // AC-718: macOS silently returns 0 from Process.HandleCount (ProcessManager.OSX.cs's EnsureHandleCountPopulated
    // has no body there) rather than throwing — same trap ProcessMemoryInfo.cs already works around for peak
    // resident. No native replacement here, so n/a rather than a misleading 0.
    private static string _HandleCountText(Process process) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? "n/a"
            : process.HandleCount.ToString(CultureInfo.InvariantCulture);

    // AC-1125 E: same "0B is a measured value, n/a is the truth" rule as handles above — Process.PrivateMemorySize64
    // reads back 0 on Unix, which is not a real reading. Internal as a test seam for the formatting itself.
    internal static string PrivText(long? privateBytes) => privateBytes is { } bytes ? _Compact(bytes) : "n/a";

    // AC-1196: same "never has" versus "measured" rule as the two above — a thread that has answered nothing yet
    // is not a thread that answered a moment ago. Internal as a test seam for the formatting itself.
    internal static string AgoText(TimeSpan? since) =>
        since is { } age ? age.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s" : "n/a";

    // AC-1125 F: a sampler with no baseline yet is not "0% CPU", it has not measured anything — see CpuSampler
    // below. Internal as a test seam for the formatting itself.
    internal static string CpuText(double? percent) =>
        percent is { } value ? value.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "n/a";

    // AC-1125 D: minutes are unreadable at "154 GB after 10653 minutes" — one compact token, no internal space.
    private static string _UptimeText(TimeSpan uptime) =>
        uptime.TotalHours >= 1 ? $"{(int)uptime.TotalHours}h{uptime.Minutes}m" : $"{(int)uptime.TotalMinutes}m";

    // Same idiom as plugins-dev/Cockpit.Plugin.SystemMonitor/SystemUsage.CpuPercent, duplicated because
    // plugins-dev sits outside Cockpit.slnx. AC-1125 F: one instance per caller now — a shared instance let
    // _LogHang and WriteSnapshot reset each other's measurement window (measured: 4.4% read where truth was 4.8-6.0%).
    internal sealed class CpuSampler
    {
        private TimeSpan _lastCpuTime = TimeSpan.Zero;
        private DateTimeOffset _lastSampledAt = DateTimeOffset.MinValue;

        // Null on the first call, not 0 — a sampler with no baseline has not measured anything yet (same
        // reasoning as handles/priv above). This is also why a hang logged before any snapshot ever ran used to
        // always read cpu=0.0%: it was every such sampler's first call.
        public double? PercentSinceLastCall()
        {
            var now = DateTimeOffset.UtcNow;
            var cpu = Environment.CpuUsage.TotalTime;

            if (_lastSampledAt == DateTimeOffset.MinValue)
            {
                (_lastCpuTime, _lastSampledAt) = (cpu, now);
                return null;
            }

            var elapsedMs = (now - _lastSampledAt).TotalMilliseconds;
            var usedMs = (cpu - _lastCpuTime).TotalMilliseconds;
            (_lastCpuTime, _lastSampledAt) = (cpu, now);

            return elapsedMs <= 0 ? 0 : Math.Clamp(usedMs / (elapsedMs * Environment.ProcessorCount) * 100, 0, 100);
        }
    }
}
