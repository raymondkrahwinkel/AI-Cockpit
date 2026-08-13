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

    /// <summary>
    /// AC-756: a heap whose <em>live</em> set sits above the floor must be compacted once, not on every check.
    /// Measured before this guard: 133 compacts/minute of ~250 ms each, which froze the UI thread over half the
    /// time and showed up as stuttering while typing.
    /// </summary>
    [Fact]
    public void CheckOnce_CompactsOnceWhileTheLiveHeapStaysAboveTheFloor()
    {
        var logger = new _CapturingLogger();
        var compacts = 0;
        var compactor = new AdaptiveGcCompactor(logger, () => 400L * 1024 * 1024, () => compacts++);

        compactor.CheckOnce();
        compactor.CheckOnce();
        compactor.CheckOnce();

        Assert.Equal(1, compacts);
        Assert.Single(logger.Messages);
    }

    [Fact]
    public void CheckOnce_CompactsAgainOnceTheHeapHasGrownPastTheLastCompact()
    {
        var logger = new _CapturingLogger();
        var compacts = 0;
        var heapBytes = 400L * 1024 * 1024;
        var compactor = new AdaptiveGcCompactor(logger, () => heapBytes, () => compacts++);

        compactor.CheckOnce();
        heapBytes = 501L * 1024 * 1024;
        compactor.CheckOnce();

        Assert.Equal(2, compacts);
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
