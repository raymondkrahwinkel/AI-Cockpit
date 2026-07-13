namespace Cockpit.Core.Diagnostics;

/// <summary>
/// Turns two samples into the number an operator reads (#78): how busy this was, as a share of one machine.
/// Pure, because the arithmetic is where a CPU meter usually lies — a process using two cores flat out is at
/// 200% of a core, and reporting that as "200%" of the machine is nonsense on a 12-core box.
/// </summary>
public static class CpuPercent
{
    /// <summary>
    /// The share of the whole machine used between two samples. 100 means every core was busy; 8 on a 12-core
    /// machine means roughly one core. Returns 0 rather than nonsense when time did not move or the sample went
    /// backwards (a process that died and whose id was reused).
    /// </summary>
    public static double Between(ResourceSample previous, ResourceSample current, TimeSpan elapsed, int processorCount)
    {
        if (elapsed <= TimeSpan.Zero || processorCount <= 0)
        {
            return 0;
        }

        var cpu = current.CpuTime - previous.CpuTime;
        if (cpu <= TimeSpan.Zero)
        {
            return 0;
        }

        var share = cpu.TotalSeconds / (elapsed.TotalSeconds * processorCount) * 100;

        // A sample can straddle a moment where a child process ended, briefly reporting more CPU time than the
        // wall clock allows. Clamp rather than show 340%.
        return Math.Clamp(share, 0, 100);
    }
}
