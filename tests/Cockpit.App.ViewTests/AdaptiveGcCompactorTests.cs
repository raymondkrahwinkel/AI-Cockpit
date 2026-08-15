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
        // Quiet allocation (a constant → zero delta) so the lull-gate lets the compact through deterministically.
        var compactor = new AdaptiveGcCompactor(logger, () => 400L * 1024 * 1024, () => compacts++, allocatedBytesProbe: () => 0L);

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
        var compactor = new AdaptiveGcCompactor(logger, () => heapBytes, () => compacts++, allocatedBytesProbe: () => 0L);

        compactor.CheckOnce();
        heapBytes = 501L * 1024 * 1024;
        compactor.CheckOnce();

        Assert.Equal(2, compacts);
    }

    /// <summary>
    /// "We shouldn't notice the GC at all" (Raymond, 2026-08-15): even a small compact is a visible micro-stutter
    /// if it lands mid-stream or mid-scroll. So a compact is deferred while the app is allocating heavily and only
    /// runs once things go quiet — the pause then overlaps a lull nobody is watching.
    /// </summary>
    [Fact]
    public void CheckOnce_DefersACompactWhileTheAppIsAllocatingHeavilyThenRunsItInTheLull()
    {
        var logger = new _CapturingLogger();
        var compacts = 0;
        var allocated = 0L;
        var compactor = new AdaptiveGcCompactor(logger, () => 450L * 1024 * 1024, () => compacts++, allocatedBytesProbe: () => allocated);

        allocated += 50L * 1024 * 1024; // a busy tick: streaming a big reply
        compactor.CheckOnce();
        Assert.Equal(0, compacts); // deferred — no pause during active work

        // No new allocation: the stream stopped, the app is idle.
        compactor.CheckOnce();
        Assert.Equal(1, compacts); // now the compact runs, unseen
    }

    /// <summary>
    /// The freeze fix (Raymond on Windows + Rick on Fedora, 2026-08-15): a blocking compacting collect of a
    /// multi-GB live heap is a 5-12 s stop-the-world pause — every logged UI-freeze hang timestamps to one of these
    /// compacts. The growth up there is the rooted Avalonia retention compaction can't free anyway. So above the
    /// compact ceiling the compactor must never compact (no matter how long it stays there); it only warns.
    /// </summary>
    [Fact]
    public void CheckOnce_NeverCompactsAMultiGigabyteHeapButWarnsInstead()
    {
        var logger = new _CapturingLogger();
        var compacts = 0;
        var heapBytes = 4L * 1024 * 1024 * 1024; // over the compact ceiling and the leak-warn ceiling
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var compactor = new AdaptiveGcCompactor(logger, () => heapBytes, () => compacts++, () => now);

        compactor.CheckOnce();
        now = now.AddSeconds(31);
        compactor.CheckOnce();
        now = now.AddSeconds(31);
        compactor.CheckOnce();

        Assert.Equal(0, compacts); // never freezes a multi-GB heap
        Assert.NotEmpty(logger.Messages); // but tells the operator a restart is the remedy
    }

    /// <summary>
    /// The pause of a compacting collect scales with heap size, so a heap under the ceiling is still a cheap hitch
    /// and gets compacted, while one over it (but not yet leak-warn territory) is left alone — no freeze, no noise.
    /// </summary>
    [Fact]
    public void CheckOnce_CompactsUnderTheCeilingButLeavesAHeapOverItAlone()
    {
        var under = new _CapturingLogger();
        var underCompacts = 0;
        new AdaptiveGcCompactor(under, () => 450L * 1024 * 1024, () => underCompacts++, allocatedBytesProbe: () => 0L).CheckOnce();
        Assert.Equal(1, underCompacts);

        var over = new _CapturingLogger();
        var overCompacts = 0;
        new AdaptiveGcCompactor(over, () => 2L * 1024 * 1024 * 1024, () => overCompacts++).CheckOnce();
        Assert.Equal(0, overCompacts);
        Assert.Empty(over.Messages); // between the compact ceiling and the leak ceiling: quiet
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
