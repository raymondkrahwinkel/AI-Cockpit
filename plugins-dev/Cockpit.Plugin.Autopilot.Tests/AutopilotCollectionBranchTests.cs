namespace Cockpit.Plugin.Autopilot.Tests;

// AC-1337, D2: a run's collection branch derives from its epic by default; the operator's explicit opt-out (or a
// run with no epic at all) leaves it null, exactly as v1 forked/published without one.
public sealed class AutopilotCollectionBranchTests
{
    [Theory]
    [InlineData(false, "AC-343", "epic/ac-343")]
    [InlineData(true, "AC-343", null)]
    public void For_DerivesFromTheEpicUnlessOptedOut(bool directToMain, string? epicId, string? expected) =>
        Assert.Equal(expected, AutopilotCollectionBranch.For(directToMain, epicId));
}
