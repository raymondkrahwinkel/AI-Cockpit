using Cockpit.Core.Diagnostics;
using FluentAssertions;

namespace Cockpit.Core.Tests.Diagnostics;

/// <summary>
/// The arithmetic behind the status bar (#78) — where a CPU meter usually lies. A process using two cores flat
/// out is at 200% of <em>a core</em>, which is meaningless on a 12-core machine; and a session's number has to
/// include the build it started, or it reads 0% at exactly the moment you look at it.
/// </summary>
public class ResourceMeterTests
{
    [Fact]
    public void CpuPercent_OneCoreFullyBusyOnAFourCoreMachine_IsAQuarterOfTheMachine()
    {
        var previous = new ResourceSample(TimeSpan.Zero, 0);
        var current = new ResourceSample(TimeSpan.FromSeconds(2), 0);

        var percent = CpuPercent.Between(previous, current, TimeSpan.FromSeconds(2), processorCount: 4);

        percent.Should().BeApproximately(25, 0.01);
    }

    [Fact]
    public void CpuPercent_EveryCoreBusy_IsOneHundred()
    {
        var previous = new ResourceSample(TimeSpan.Zero, 0);
        var current = new ResourceSample(TimeSpan.FromSeconds(8), 0);

        CpuPercent.Between(previous, current, TimeSpan.FromSeconds(2), processorCount: 4).Should().BeApproximately(100, 0.01);
    }

    [Fact]
    public void CpuPercent_WhenAChildDiedMidSample_IsClampedRatherThanAbsurd()
    {
        // A tree that briefly reports more CPU time than the wall clock allows must not show 340%.
        var previous = new ResourceSample(TimeSpan.Zero, 0);
        var current = new ResourceSample(TimeSpan.FromSeconds(30), 0);

        CpuPercent.Between(previous, current, TimeSpan.FromSeconds(2), processorCount: 4).Should().Be(100);
    }

    [Fact]
    public void CpuPercent_OnTheFirstSample_IsZeroRatherThanAGuess()
    {
        var sample = new ResourceSample(TimeSpan.FromSeconds(9), 0);

        CpuPercent.Between(sample, sample, TimeSpan.Zero, processorCount: 8).Should().Be(0);
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
        sample.CpuTime.Should().Be(TimeSpan.FromSeconds(14));
        sample.WorkingSetBytes.Should().Be(1400);
    }

    [Fact]
    public void ProcessTree_ForAProcessThatIsGone_IsNothing_BecauseAnExitedSessionIsNotAnError()
    {
        ProcessTree.Sum([new ProcessRow(1, 0, TimeSpan.FromSeconds(1), 100)], rootProcessId: 77)
            .Should().Be(ResourceSample.None);
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

        ProcessTree.Sum(rows, rootProcessId: 10).WorkingSetBytes.Should().Be(200);
    }
}
