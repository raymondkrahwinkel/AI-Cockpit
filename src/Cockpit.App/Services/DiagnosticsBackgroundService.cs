using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Services;

// AC-718: one background thread doing two independent jobs, because both need to keep running when the UI
// thread does not — the existing resource sampler is a DispatcherTimer (CockpitView.axaml.cs) and stops ticking
// the moment it is bound to freezes, precisely when this needs to keep writing.
//
// 1. Periodic diagnostics line (opt-in, DebugSettings.LogDiagnosticSnapshots): memory/GC/handles/threads, one
//    grep-able key=value line every SnapshotInterval, reusing the same reader the Debug tab uses
//    (DiagnosticsCollector.SelfReadSnapshot) so the panel and the log cannot disagree about what the process
//    weighs. Off by default; SetSnapshotsEnabled is a plain volatile-bool flip, so a tick with it off costs one
//    bool read.
// 2. UI-thread freeze heartbeat (always on, costs nothing while healthy): posts a low-priority ping to the
//    dispatcher every tick and watches how long it takes to land. Hysteresis lives in UiThreadHeartbeat.Decide,
//    a pure function, so it is testable without an actual frozen UI thread.
//
// Not an IHostedService: those start synchronously from Program.StartHostedServices, ahead of
// BuildAvaloniaApp().Start... — touching Dispatcher.UIThread that early races Avalonia's own platform setup for
// ownership of the dispatcher and crashes the app on launch (measured: "The calling thread cannot access this
// object because a different thread owns it", thrown out of Avalonia's own Setup()). Same reasoning
// ScheduledResumeCoordinator already documents for the same reason. Start() is instead called from
// App.axaml.cs, once OnFrameworkInitializationCompleted is running — by then Avalonia's Setup() has already
// bound Dispatcher.UIThread to the real UI thread.
public sealed class DiagnosticsBackgroundService : ISingletonService, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(10);

    private readonly ILogger<DiagnosticsBackgroundService> _logger;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private Thread? _thread;
    private volatile bool _stopping;
    private volatile bool _snapshotsEnabled;

    // Written from the dispatcher post below (UI thread), read from the background thread — Interlocked rather
    // than a plain field because a torn 64-bit read across two threads is a real possibility. -1 means "no
    // successful pong yet", which is also how the loop knows not to arm the heartbeat too early (see _Run):
    // before the desktop lifetime's dispatcher loop is pumping, every value here would read as an infinite hang.
    private long _lastPongTicks = -1;

    public DiagnosticsBackgroundService(ILogger<DiagnosticsBackgroundService> logger)
    {
        _logger = logger;
    }

    // Flips the periodic-snapshot half on or off live — called by CockpitViewModel from the Options checkbox and
    // at startup load. The heartbeat half is not gated by this; it always runs.
    public void SetSnapshotsEnabled(bool enabled) => _snapshotsEnabled = enabled;

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

                if (_snapshotsEnabled && now >= nextSnapshotAt)
                {
                    _WriteSnapshot(cpu);
                    nextSnapshotAt = now + SnapshotInterval;
                }
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

    private void _WriteSnapshot(_CpuSampler cpu)
    {
        var snapshot = DiagnosticsCollector.SelfReadSnapshot();
        var memory = snapshot.Memory;
        var heap = snapshot.ManagedHeap;

        using var process = Process.GetCurrentProcess();

        _logger.LogInformation(
            "diag rss={Rss} peak={Peak} virt={Virt} priv={Priv} heap={Heap} live={Live} alloc={Alloc} " +
            "gc={Gen0}/{Gen1}/{Gen2} gcpause={GcPause:0.0}% handles={Handles} threads={Threads} tp={Pending}/{ThreadPoolCount} cpu={Cpu:0.0}%",
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
            cpu.PercentSinceLastCall());
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

    // AC-718: macOS never populates Process.HandleCount — ProcessManager.OSX.cs's EnsureHandleCountPopulated has
    // no body there, so it silently returns 0 rather than throwing. Same trap ProcessMemoryInfo.cs already works
    // around for peak resident; here there is no native replacement, so the log says n/a instead of a
    // misleading 0 that would read as "no handles open".
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
