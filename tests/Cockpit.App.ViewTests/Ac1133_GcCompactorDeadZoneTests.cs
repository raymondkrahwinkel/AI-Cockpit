using Cockpit.App.Services;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.ViewTests;

public class Ac1133_GcCompactorDeadZoneTests
{
    private const long MB = 1024L * 1024L;

    [Fact]
    public void CheckOnce_CompactsAgainAfterADeadZoneIsRearmed()
    {
        var compacts = 0;
        var heapBytes = 450 * MB;
        var compactor = new AdaptiveGcCompactor(
            new _Silent(),
            () => heapBytes,
            () =>
            {
                compacts++;
                heapBytes = heapBytes * 98 / 100;
            },
            allocatedBytesProbe: () => 0L);

        compactor.CheckOnce();
        var armed = compacts;

        heapBytes = 400 * MB;
        compactor.CheckOnce();
        heapBytes = 500 * MB;
        compactor.CheckOnce();

        Assert.Equal(armed + 1, compacts);
    }

    [Fact]
    public void CheckOnce_DoesNotCompactEveryCheckWhenTheLiveHeapStaysHigh()
    {
        var compacts = 0;
        const long heapBytes = 511 * MB;
        var compactor = new AdaptiveGcCompactor(
            new _Silent(), () => heapBytes, () => compacts++, allocatedBytesProbe: () => 0L);

        compactor.CheckOnce();
        for (var check = 0; check < 10; check++)
        {
            compactor.CheckOnce();
        }

        Assert.Equal(1, compacts);
    }

    private sealed class _Silent : ILogger<AdaptiveGcCompactor>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }
}
