using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure.Tests.Sessions;

/// <summary>
/// The macOS watchdog's logic (AC-661), driven a sweep at a time against a fake process table — no timer, no real
/// process. It used to kill a session over its cap; AC-692 retired that, so this now proves the opposite: nothing
/// here ever touches a real OS process, only reports. What no test here can answer is whether <c>ps</c> reports
/// these figures on real macOS hardware; there is no Mac to check that on, the same accepted blind spot as AC-57.
/// </summary>
public class PollingMemoryLimiterTests
{
    private const long Megabyte = 1024 * 1024;

    [Fact]
    public void ATreeOverItsCapIsReported_AndOneUnderItIsLeftAlone()
    {
        // 300 MB of parent plus 900 MB of grandchild against a 1 GB cap: the sum is the point, since the process
        // that blows up is the build the agent started rather than the agent.
        var table = new FakeProcessTable(
            new ProcessRow(100, 1, TimeSpan.Zero, 300 * Megabyte),
            new ProcessRow(101, 100, TimeSpan.Zero, 900 * Megabyte));

        using var limiter = new PollingMemoryLimiter(table, NullLogger<PollingMemoryLimiter>.Instance);

        using var under = limiter.Apply(100, 4096 * Megabyte);
        Assert.Equal(0, limiter.CheckOnce());

        using var over = limiter.Apply(100, 1024 * Megabyte);
        Assert.Equal(1, limiter.CheckOnce());
    }

    [Fact]
    public void AReportedSessionIsNotReportedAgainOnTheNextSweep_WhileStillOverCap()
    {
        // Otherwise a session sitting over its cap logs the same warning every 1.5 seconds for as long as it
        // stays there — the same one-shot shape as every other memory warning in this codebase.
        var table = new FakeProcessTable(new ProcessRow(200, 1, TimeSpan.Zero, 2048 * Megabyte));
        using var limiter = new PollingMemoryLimiter(table, NullLogger<PollingMemoryLimiter>.Instance);

        using var watch = limiter.Apply(200, 512 * Megabyte);

        Assert.Equal(1, limiter.CheckOnce());
        Assert.Equal(0, limiter.CheckOnce());
    }

    [Fact]
    public void OnceBackUnderCap_TheNextCrossingIsReportedAgain()
    {
        var table = new FakeProcessTable();
        using var limiter = new PollingMemoryLimiter(table, NullLogger<PollingMemoryLimiter>.Instance);

        using var watch = limiter.Apply(210, 512 * Megabyte);

        table.Rows = [new ProcessRow(210, 1, TimeSpan.Zero, 2048 * Megabyte)];
        Assert.Equal(1, limiter.CheckOnce());
        Assert.Equal(0, limiter.CheckOnce());

        table.Rows = [new ProcessRow(210, 1, TimeSpan.Zero, 100 * Megabyte)];
        Assert.Equal(0, limiter.CheckOnce());

        table.Rows = [new ProcessRow(210, 1, TimeSpan.Zero, 2048 * Megabyte)];
        Assert.Equal(1, limiter.CheckOnce());
    }

    [Fact]
    public void AReleasedWatchIsDropped_SoAnEndedSessionIsNeverReportedLate()
    {
        var table = new FakeProcessTable(new ProcessRow(300, 1, TimeSpan.Zero, 2048 * Megabyte));
        using var limiter = new PollingMemoryLimiter(table, NullLogger<PollingMemoryLimiter>.Instance);

        limiter.Apply(300, 512 * Megabyte)!.Dispose();

        Assert.Equal(0, limiter.CheckOnce());
    }

    [Fact]
    public void NoProcessIsEverTouched_TheSweepOnlyReports()
    {
        // AC-692: the whole point is that this class no longer has any way to end a process. A pid that does not
        // exist on this machine going over its cap must not throw — if it did, something in here was still trying
        // to reach out to a real OS process instead of only reading the fake table.
        var table = new FakeProcessTable(new ProcessRow(int.MaxValue, 1, TimeSpan.Zero, 2048 * Megabyte));
        using var limiter = new PollingMemoryLimiter(table, NullLogger<PollingMemoryLimiter>.Instance);

        using var watch = limiter.Apply(int.MaxValue, 512 * Megabyte);

        Assert.Equal(1, limiter.CheckOnce());
    }

    [Fact]
    public void WithNothingWatched_TheProcessTableIsNotEvenRead()
    {
        // The sweep runs for every session on the machine; a cockpit with no capped session must not pay for it.
        var table = new FakeProcessTable();
        using var limiter = new PollingMemoryLimiter(table, NullLogger<PollingMemoryLimiter>.Instance);

        Assert.Equal(0, limiter.CheckOnce());
        Assert.Equal(0, table.Reads);
    }

    private sealed class FakeProcessTable(params ProcessRow[] rows) : IProcessTableReader
    {
        public IReadOnlyList<ProcessRow> Rows { get; set; } = rows;

        public int Reads { get; private set; }

        public IReadOnlyList<ProcessRow> Read()
        {
            Reads++;
            return Rows;
        }
    }
}
