using Cockpit.App.Services;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-733: the adaptive compact must skip a heap too small to be worth its own pause — a xunit test process's
/// managed heap sits well under the 300 MB floor, so <see cref="AdaptiveGcCompactor.CheckOnce"/> running here is
/// itself the case this guards.
/// </summary>
public class AdaptiveGcCompactorTests
{
    [Fact]
    public void CheckOnce_SkipsACompactWhenTheHeapIsBelowTheFloor()
    {
        var logger = new _CapturingLogger();
        var compactor = new AdaptiveGcCompactor(logger);

        compactor.CheckOnce();

        Assert.Empty(logger.Messages);
    }

    private sealed class _CapturingLogger : ILogger<AdaptiveGcCompactor>
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
