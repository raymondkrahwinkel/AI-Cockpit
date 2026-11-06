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
