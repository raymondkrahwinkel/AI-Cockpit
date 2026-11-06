using Cockpit.Core.Diagnostics;

namespace Cockpit.Core.Tests.Diagnostics;

/// <summary>
/// Tests <see cref="OomdKillLine"/> against the real journal lines of 2026-08-24 and 2026-08-25, copied verbatim.
/// This parser is the whole of criterion 2: a cgroup kill leaves no exit code and no oom_kill count to read.
/// </summary>
public class OomdKillLineTests
{
    private const string RealLine =
        "Killed /user.slice/user-1000.slice/user@1000.service/app.slice/cockpit-session-481979 due to memory "
        + "pressure for /user.slice/user-1000.slice/user@1000.service/app.slice being 82.71% > 80.00% for > 20s "
        + "with reclaim activity";

    [Fact]
    public void ARealKillLine_NamesTheGroupCockpitCreated_AndKeepsOomdsOwnAccountOfWhy()
    {
        var kill = OomdKillLine.Parse(RealLine);

        Assert.NotNull(kill);
        Assert.Equal("cockpit-session-481979", kill.CgroupName);

        // The reason is kept verbatim rather than re-worded, so the row in the session and the journal say the
        // same thing.
        Assert.Equal("82.71% > 80.00% for > 20s with reclaim activity", kill.Pressure);
    }
}
