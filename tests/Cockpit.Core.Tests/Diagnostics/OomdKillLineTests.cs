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

    // The last row is the one that matters: a trailing slash leaves no name behind it, and a kill of nothing must
    // not match a session of ours.
    [Theory]
    [InlineData("Considered 4 cgroups for memory pressure kill, top candidate was app.slice")]
    [InlineData("")]
    [InlineData("Killed something without a reason")]
    [InlineData("Killed / due to memory pressure for /app.slice being 90% > 80.00%")]
    public void AnythingElseOomdLogs_IsNotAKill(string line) =>
        Assert.Null(OomdKillLine.Parse(line));

    [Fact]
    public void AKillOfSomethingElse_KeepsItsOwnName() =>
        // The caller matches this against the group it created; a kill of another app must not read as ours.
        Assert.Equal(
            "firefox.scope",
            OomdKillLine.Parse("Killed /user.slice/user-1000.slice/app.slice/firefox.scope due to memory pressure for /x being 90% > 80.00%")!.CgroupName);
}
