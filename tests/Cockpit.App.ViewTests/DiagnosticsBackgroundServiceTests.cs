using Cockpit.App.Services;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.ViewTests;

// AC-1125: the diag-line ticket. Each test below is the counter-proof named in the ticket's acceptance section —
// the comment on each documents which point it pins and what fails without the fix.
//
// [Collection("avalonia")]: Start() below runs the real background thread, which posts to Dispatcher.UIThread —
// that needs the fixture's pumping headless UI thread to be up, even though this test never touches a control.
[Collection("avalonia")]
public class DiagnosticsBackgroundServiceTests
{
    // A: fails today (zero lines) — the thread ran no line proving it was alive before the first 10s snapshot.
    [Fact]
    public async Task Start_LogsARunningLineWithinTwoTicksEvenBeforeAnySnapshot()
    {
        var logger = new _CapturingLogger();
        var service = new DiagnosticsBackgroundService(logger);

        try
        {
            service.Start();

            var deadline = DateTime.UtcNow.AddSeconds(2.5);
            while (DateTime.UtcNow < deadline && !logger.Messages.Any(m => m.Contains("snapshots=")))
            {
                await Task.Delay(50);
            }

            Assert.Contains(logger.Messages, m => m.Contains("snapshots="));
        }
        finally
        {
            service.Dispose();
        }
    }

    // C: bounded — a heap over the ceiling must skip the forced full collection and log why, not silently run it.
    [Fact]
    public void SampleHangRetention_SkipsAndLogsAboveTheHeapCeiling()
    {
        var logger = new _CapturingLogger();
        var service = new DiagnosticsBackgroundService(
            logger, heapBytesProbe: () => DiagnosticsBackgroundService.HangGcSampleCeilingBytes + 1);

        var text = service.SampleHangRetention();

        Assert.Equal("skipped", text);
        Assert.Contains(logger.Messages, m => m.Contains("skipped"));
    }

    // C: still runs under the ceiling, and reports what it measured.
    [Fact]
    public void SampleHangRetention_RunsAndReportsUnderTheHeapCeiling()
    {
        var service = new DiagnosticsBackgroundService(new _CapturingLogger(), heapBytesProbe: () => 10L * 1024 * 1024);

        var text = service.SampleHangRetention();

        Assert.Contains("->", text);
    }

    // D: the three added fields, in a line still short enough to paste in a chat message.
    [Fact]
    public void WriteSnapshot_IncludesUptimeSessionCountAndLayoutStand()
    {
        var logger = new _CapturingLogger();
        var service = new DiagnosticsBackgroundService(logger);
        service.SetSessionContext(() => (3, "focus+rail"));

        service.WriteSnapshot(new DiagnosticsBackgroundService.CpuSampler(), renderClockStalled: false);

        var line = Assert.Single(logger.Messages);
        Assert.Contains("uptime=", line);
        Assert.Contains("sessions=3", line);
        Assert.Contains("layout=focus+rail", line);
    }

    // E: `live=` renamed off the diag line — it was never a retention measurement (do not re-derive that it is).
    [Fact]
    public void WriteSnapshot_NoLongerNamesTheHeapFieldLive()
    {
        var logger = new _CapturingLogger();
        var service = new DiagnosticsBackgroundService(logger);

        service.WriteSnapshot(new DiagnosticsBackgroundService.CpuSampler(), renderClockStalled: false);

        var line = Assert.Single(logger.Messages);
        Assert.Contains("managed=", line);
        Assert.DoesNotContain("live=", line);
    }

    // E: 0B is a measured value, n/a is the truth — same rule now applied to priv= as already applied to handles=.
    [Fact]
    public void PrivText_ReportsNaInsteadOfAMisleadingZero()
    {
        Assert.Equal("n/a", DiagnosticsBackgroundService.PrivText(null));
        Assert.Equal("1.0KB", DiagnosticsBackgroundService.PrivText(1024));
    }

    // F: fails today — the first call on a fresh sampler reported 0.0%, indistinguishable from "measured, and idle".
    [Fact]
    public void CpuSampler_FirstCallReportsNoBaselineInsteadOfZero()
    {
        var sampler = new DiagnosticsBackgroundService.CpuSampler();

        Assert.Null(sampler.PercentSinceLastCall());
        Assert.Equal("n/a", DiagnosticsBackgroundService.CpuText(null));
        Assert.Equal("0.0%", DiagnosticsBackgroundService.CpuText(0));
    }

    // F: fails today — one shared sampler let _LogHang and WriteSnapshot reset each other's measurement window.
    // Two independent instances (the fix) each keep their own baseline regardless of what the other one does.
    [Fact]
    public void CpuSampler_TwoIndependentInstancesDoNotResetEachOthersBaseline()
    {
        var hang = new DiagnosticsBackgroundService.CpuSampler();
        var snapshot = new DiagnosticsBackgroundService.CpuSampler();

        Assert.Null(hang.PercentSinceLastCall());
        Thread.Sleep(20);
        Assert.Null(snapshot.PercentSinceLastCall());

        Thread.Sleep(20);
        Assert.NotNull(hang.PercentSinceLastCall());
        Assert.NotNull(snapshot.PercentSinceLastCall());
    }

    private sealed class _CapturingLogger : ILogger<DiagnosticsBackgroundService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => _NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

        private sealed class _NullScope : IDisposable
        {
            public static readonly _NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
