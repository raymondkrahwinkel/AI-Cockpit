using Cockpit.Core.Diagnostics;

namespace Cockpit.Core.Tests.Diagnostics;

/// <summary>
/// The arithmetic behind the status bar (#78) — where a CPU meter usually lies. A process using two cores flat
/// out is at 200% of <em>a core</em>, which is meaningless on a 12-core machine; and a session's number has to
/// include the build it started, or it reads 0% at exactly the moment you look at it.
/// </summary>
public class ResourceMeterTests
{
    // A share of the machine, not of a core: one core flat out on four is 25%, four cores is 100%. A tree
    // reporting more CPU time than the wall clock allows (a child that died mid-sample) is clamped rather than
    // shown as 340%, and the first sample, with no elapsed time behind it, is zero rather than a guess.
    [Theory]
    [InlineData(0, 2, 2, 4, 25)]
    [InlineData(0, 8, 2, 4, 100)]
    [InlineData(0, 30, 2, 4, 100)]
    [InlineData(9, 9, 0, 8, 0)]
    public void CpuPercent_IsAShareOfTheWholeMachine_ClampedToIt(
        int previousSeconds, int currentSeconds, int elapsedSeconds, int processorCount, double expected)
    {
        var previous = new ResourceSample(TimeSpan.FromSeconds(previousSeconds), 0);
        var current = new ResourceSample(TimeSpan.FromSeconds(currentSeconds), 0);

        var percent = CpuPercent.Between(previous, current, TimeSpan.FromSeconds(elapsedSeconds), processorCount);

        Assert.Equal(expected, percent, 2);
    }

    [Fact]
    public void ProcessTree_AddsUpTheChildrenAndTheirChildren()
    {
        // A claude session (10) that shelled out to a build (20), which forked a compiler (30).
        var rows = new List<ProcessRow>
        {
            new(1, 0, TimeSpan.FromSeconds(1), 100),
            new(10, 1, TimeSpan.FromSeconds(2), 200),
            new(20, 10, TimeSpan.FromSeconds(4), 400),
            new(30, 20, TimeSpan.FromSeconds(8), 800),
            new(99, 1, TimeSpan.FromSeconds(16), 1600),
        };

        var sample = ProcessTree.Sum(rows, rootProcessId: 10);

        // The session, the build and the compiler — but not the unrelated process 99.
        Assert.Equal(TimeSpan.FromSeconds(14), sample.CpuTime);
        Assert.Equal(1400, sample.WorkingSetBytes);
    }

    [Fact]
    public void ProcessTree_ForAProcessThatIsGone_IsNothing_BecauseAnExitedSessionIsNotAnError()
    {
        Assert.Equal(
            ResourceSample.None,
            ProcessTree.Sum([new ProcessRow(1, 0, TimeSpan.FromSeconds(1), 100)], rootProcessId: 77));
    }

    [Fact]
    public void ProcessTree_WithACycleInTheTable_Terminates()
    {
        // A reused process id can make the table describe a loop; the walk must still end.
        var rows = new List<ProcessRow>
        {
            new(10, 20, TimeSpan.FromSeconds(1), 100),
            new(20, 10, TimeSpan.FromSeconds(1), 100),
        };

        Assert.Equal(200, ProcessTree.Sum(rows, rootProcessId: 10).WorkingSetBytes);
    }
}
