using Cockpit.Core.HeavyRuns;

namespace Cockpit.Core.Tests.HeavyRuns;

// AC-1535, layer 2 (calculations): who the machine-wide queue lets in on the measured load, and the CPU share that
// load is computed from, read from samples as /proc/stat and GetSystemTimes hand them out.
public class HeavyRunAdmissionTests
{
    private const long Gigabyte = 1L << 30;

    // admission follows arrival sequence even when candidates are supplied out of order.
    [Theory]
    [InlineData(85, true, 1, 0, -1, -1, "waiting: CPU 85%")]
    [InlineData(95, false, 1, -1, -1, 0, null)]
    [InlineData(40, true, 1, -1, -1, -1, "waiting: journeys has no measured cost yet, so it runs alone")]
    [InlineData(40, true, 1, 10, 2, 0, null)]
    [InlineData(40, true, 2, 10, 1, 1, null)]
    public void TheHeadOfTheLineGetsInOnlyWhenItsCostFitsUnderTheCeiling_UnlessNothingHeavyRuns(
        double cpu, bool isAnyRunning, long firstArrival, double cost,
        long secondArrival, int expectedAdmit, string? expectedReason)
    {
        var known = cost < 0 ? null : new HeavyRunCost(cost, Gigabyte, 0);
        List<HeavyRunCandidate> waiting = [new("journeys", firstArrival, known, 100 * Gigabyte)];
        if (secondArrival >= 0)
        {
            waiting.Add(new HeavyRunCandidate("journeys", secondArrival, known, 100 * Gigabyte));
        }

        var decision = HeavyRunAdmission.Next(waiting, new MachineLoad(cpu, 8 * Gigabyte, 16 * Gigabyte), new HeavyRunSettings(), isAnyRunning ? 1 : 0);

        Assert.Equal((expectedAdmit < 0 ? null : expectedAdmit, expectedReason), (decision.Admit, decision.WaitReason));
    }

    // Category C: AC-2174's third run waits for one of two occupied places, even on an idle machine.
    [Fact]
    public void AParallelLimitHoldsTheNextRunUntilAPlaceIsReturned_AndZeroKeepsAutomaticAdmission()
    {
        List<HeavyRunCandidate> waiting = [new("build", 1, new HeavyRunCost(20, Gigabyte, 0), 100 * Gigabyte)];
        var load = new MachineLoad(40, 8 * Gigabyte, 16 * Gigabyte);
        var settings = new HeavyRunSettings { MaxParallelRuns = 2 };

        var full = HeavyRunAdmission.Next(waiting, load, settings, 2);
        Assert.Null(full.Admit);
        Assert.Contains("maximum 2 parallel runs", full.WaitReason);
        Assert.Equal(0, HeavyRunAdmission.Next(waiting, load, settings, 1).Admit);
        Assert.Equal(0, HeavyRunAdmission.Next(waiting, load, settings with { MaxParallelRuns = 0 }, 2).Admit);
        Assert.Equal(0, HeavyRunAdmission.Next(waiting, load with { CpuPercent = 100 }, settings, 0).Admit);
    }
}
