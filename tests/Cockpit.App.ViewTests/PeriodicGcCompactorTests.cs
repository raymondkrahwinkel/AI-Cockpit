using Cockpit.App.Services;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-733: the periodic compact must skip a heap that is too small to be worth its own pause — a xunit test
/// process's managed heap sits well under the 512 MB floor, so <see cref="PeriodicGcCompactor.RunOnce"/> running
/// here is itself the case this guards.
/// </summary>
public class PeriodicGcCompactorTests
{
    [Fact]
    public void RunOnce_SkipsACompactWhenTheHeapIsBelowTheFloor()
    {
        var logger = new _CapturingLogger();
        var compactor = new PeriodicGcCompactor(logger);

        compactor.RunOnce();

        Assert.Empty(logger.Messages);
    }

    private sealed class _CapturingLogger : ILogger<PeriodicGcCompactor>
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
