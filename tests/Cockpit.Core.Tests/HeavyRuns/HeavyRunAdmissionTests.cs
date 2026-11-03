using Cockpit.Core.HeavyRuns;

namespace Cockpit.Core.Tests.HeavyRuns;

// AC-1535, layer 2 (calculations): who the machine-wide queue lets in on the measured load, and the CPU share that
// load is computed from, read from samples as /proc/stat and GetSystemTimes hand them out.
public class HeavyRunAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private const long Gigabyte = 1L << 30;

    // A cost below zero is a kind never measured; a second candidate waited -1 minutes when there is none.
    [Theory]
    [InlineData(40, true, false, 1, 20, false, -1, 0, null)]
    [InlineData(85, true, false, 1, 0, false, -1, -1, "waiting: CPU 85%")]
    [InlineData(95, false, false, 1, -1, false, -1, 0, null)]
    [InlineData(40, true, false, 1, -1, false, -1, -1, "waiting: journeys has no measured cost yet, so it runs alone")]
    [InlineData(40, true, false, 5, 10, true, 1, 1, null)]
    [InlineData(40, true, false, 16, 10, true, 1, 0, null)]
    public void TheHeadOfTheLineGetsInOnlyWhenItsCostFitsUnderTheCeiling_UnlessNothingHeavyRuns(
        double cpu, bool isAnyRunning, bool firstIsPreMerge, int firstWaitedMinutes, double cost,
        bool secondIsPreMerge, int secondWaitedMinutes, int expectedAdmit, string? expectedReason)
    {
        var known = cost < 0 ? null : new HeavyRunCost(cost, Gigabyte, 0);
        List<HeavyRunCandidate> waiting = [new("journeys", firstIsPreMerge, Now.AddMinutes(-firstWaitedMinutes), known, 100 * Gigabyte)];
        if (secondWaitedMinutes >= 0)
        {
            waiting.Add(new HeavyRunCandidate("journeys", secondIsPreMerge, Now.AddMinutes(-secondWaitedMinutes), known, 100 * Gigabyte));
        }

        var decision = HeavyRunAdmission.Next(waiting, new MachineLoad(cpu, 8 * Gigabyte, 16 * Gigabyte), new HeavyRunSettings(), isAnyRunning, Now);

        Assert.Equal((expectedAdmit < 0 ? null : expectedAdmit, expectedReason), (decision.Admit, decision.WaitReason));
    }
}
