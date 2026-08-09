using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure.Tests.Sessions;

/// <summary>
/// The macOS watchdog's logic (AC-661), driven a sweep at a time against a fake process table — no timer, no real
/// process. What no test here can answer is whether <c>ps</c> reports these figures on real macOS hardware; there
/// is no Mac to check that on, the same accepted blind spot as AC-57.
/// </summary>
public class PollingMemoryLimiterTests
{
    private const long Megabyte = 1024 * 1024;

    [Fact]
    public void ATreeOverItsCapIsKilled_AndOneUnderItIsLeftAlone()
    {
        // 300 MB of parent plus 900 MB of grandchild against a 1 GB cap: the sum is the point, since the process
        // that blows up is the build the agent started rather than the agent.
        var table = new FakeProcessTable(
            new ProcessRow(100, 1, TimeSpan.Zero, 300 * Megabyte),
            new ProcessRow(101, 100, TimeSpan.Zero, 900 * Megabyte));

        var killed = new List<int>();
        using var limiter = new PollingMemoryLimiter(table, NullLogger.Instance, killed.Add);

        using var under = limiter.Apply(100, 4096 * Megabyte);
        Assert.Equal(0, limiter.CheckOnce());
        Assert.Empty(killed);

        using var over = limiter.Apply(100, 1024 * Megabyte);
        Assert.Equal(1, limiter.CheckOnce());
        Assert.Equal([100], killed);
    }

    [Fact]
    public void AKilledSessionIsNotKilledAgainOnTheNextSweep()
    {
        // The process table still shows the tree for a moment after a kill; killing twice would report a second
        // session dying that never did.
        var table = new FakeProcessTable(new ProcessRow(200, 1, TimeSpan.Zero, 2048 * Megabyte));
        var killed = new List<int>();
        using var limiter = new PollingMemoryLimiter(table, NullLogger.Instance, killed.Add);

        using var watch = limiter.Apply(200, 512 * Megabyte);

        Assert.Equal(1, limiter.CheckOnce());
        Assert.Equal(0, limiter.CheckOnce());
        Assert.Equal([200], killed);
    }

    [Fact]
    public void AReleasedWatchIsDropped_SoAnEndedSessionIsNeverKilledLate()
    {
        var table = new FakeProcessTable(new ProcessRow(300, 1, TimeSpan.Zero, 2048 * Megabyte));
        var killed = new List<int>();
        using var limiter = new PollingMemoryLimiter(table, NullLogger.Instance, killed.Add);

        limiter.Apply(300, 512 * Megabyte)!.Dispose();

        Assert.Equal(0, limiter.CheckOnce());
        Assert.Empty(killed);
    }

    [Fact]
    public void AFailingKillIsSurvived_SoOneStuckSessionDoesNotStopTheSweep()
    {
        // A pid that has already exited throws from the real kill; the watchdog must go on watching the rest.
        var table = new FakeProcessTable(
            new ProcessRow(400, 1, TimeSpan.Zero, 2048 * Megabyte),
            new ProcessRow(401, 1, TimeSpan.Zero, 2048 * Megabyte));

        var killed = new List<int>();
        using var limiter = new PollingMemoryLimiter(
            table,
            NullLogger.Instance,
            processId =>
            {
                if (processId == 400)
                {
                    throw new InvalidOperationException("already gone");
                }

                killed.Add(processId);
            });

        using var first = limiter.Apply(400, 512 * Megabyte);
        using var second = limiter.Apply(401, 512 * Megabyte);

        Assert.Equal(2, limiter.CheckOnce());
        Assert.Equal([401], killed);
    }

    [Fact]
    public void WithNothingWatched_TheProcessTableIsNotEvenRead()
    {
        // The sweep runs for every session on the machine; a cockpit with no capped session must not pay for it.
        var table = new FakeProcessTable();
        using var limiter = new PollingMemoryLimiter(table, NullLogger.Instance, _ => { });

        Assert.Equal(0, limiter.CheckOnce());
        Assert.Equal(0, table.Reads);
    }

    private sealed class FakeProcessTable(params ProcessRow[] rows) : IProcessTableReader
    {
        public int Reads { get; private set; }

        public IReadOnlyList<ProcessRow> Read()
        {
            Reads++;
            return rows;
        }
    }
}
