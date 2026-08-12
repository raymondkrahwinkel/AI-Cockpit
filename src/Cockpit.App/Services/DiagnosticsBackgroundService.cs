using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
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

    private readonly ILogger<DiagnosticsBackgroundService> _logger;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private Thread? _thread;
    private volatile bool _stopping;
    private volatile bool _snapshotsEnabled;

    // Written on the UI thread (dispatcher post below), read on the background thread — Interlocked guards the
    // cross-thread 64-bit access. -1 means "no pong yet", which _Run uses to avoid arming the heartbeat before
    // the dispatcher loop is even pumping.
    private long _lastPongTicks = -1;

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
