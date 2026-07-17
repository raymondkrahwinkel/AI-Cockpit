using Cockpit.Infrastructure.Diagnostics;
using FluentAssertions;

namespace Cockpit.Infrastructure.Tests.Diagnostics;

/// <summary>
/// The Linux crash-log reader shells out to coredumpctl and journalctl (AC-58), which may be absent or refuse the
/// read — so its whole contract is that it never throws and returns "nothing found" instead. This runs the real
/// tools on the build machine: on a healthy machine that is an empty list, which is the point (a cockpit that has
/// not crashed has no artifacts), and either way it must come back without an exception.
/// </summary>
public class LinuxCrashLogReaderTests
{
    [Fact]
    public void RecentEntries_OnLinux_ReturnsWithoutThrowing()
    {
        // OperatingSystem.IsLinux() (not RuntimeInformation) is the guard the platform-compatibility analyzer
        // recognises, so the call to the linux-only reader below is seen as safe rather than flagged (CA1416).
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var reader = new LinuxCrashLogReader();

        // Calling it directly is the no-throw assertion — an exception from the real coredumpctl/journalctl shell-out
        // would fail the test here. The contract is then just: a list, capped at the requested count.
        var entries = reader.RecentEntries(3);

        entries.Should().NotBeNull().And.HaveCountLessThanOrEqualTo(3);
    }
}
