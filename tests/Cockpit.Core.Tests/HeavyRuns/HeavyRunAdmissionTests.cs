using System.Globalization;
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

    // Windows samples are written "idle kernel user"; /proc/stat samples as the file's own `cpu` line.
    [Theory]
    [InlineData(true, "cpu  100 0 50 800 50 0 0 0 0 0", "cpu  200 0 100 850 50 0 0 0 0 0", 75)]
    [InlineData(false, "800 1000 200", "900 1150 250", 50)]
    public void TheBusyShareIsTheNonIdlePartOfTheTimeBetweenTwoSamples(bool isProcStat, string before, string after, double expected)
    {
        CpuTimes Read(string sample)
        {
            if (isProcStat)
            {
                return CpuTimes.FromProcStat(sample) ?? throw new InvalidOperationException("No cpu line.");
            }

            var times = sample.Split(' ').Select(time => ulong.Parse(time, CultureInfo.InvariantCulture)).ToArray();
            return CpuTimes.FromSystemTimes(times[0], times[1], times[2]);
        }

        Assert.Equal(expected, CpuTimes.BusyPercent(Read(before), Read(after)), precision: 6);
    }
}
