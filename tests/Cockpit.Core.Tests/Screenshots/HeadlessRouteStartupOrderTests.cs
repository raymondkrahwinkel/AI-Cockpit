using Cockpit.TestSupport;

namespace Cockpit.Core.Tests.Screenshots;

public sealed class HeadlessRouteStartupOrderTests
{
    // AC-1235: the one fact about startup no other test can see. Slide this call back below the guard and every
    // suite stays green while `--screenshot` goes silent again on any machine with a cockpit already running.
    [Fact]
    public void TheHeadlessRoutes_AreAnsweredBeforeTheSingleInstanceGuardIsTaken()
    {
        var program = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "src", "Cockpit.App", "Program.cs"));

        var routes = program.IndexOf("HeadlessRoutes.TryRun(", StringComparison.Ordinal);
        var guard = program.IndexOf("SingleInstanceGuard.TryAcquire(", StringComparison.Ordinal);

        Assert.True(
            routes >= 0,
            "Program.cs no longer calls HeadlessRoutes.TryRun, so nothing keeps a screenshot, a calibration or a "
            + "dictation run above the single-instance guard any more.");
        Assert.True(
            guard >= 0,
            "Program.cs no longer calls SingleInstanceGuard.TryAcquire, so this test is reading for something that "
            + "has moved and is proving nothing.");
        Assert.True(
            routes < guard,
            "HeadlessRoutes.TryRun must be called before SingleInstanceGuard.TryAcquire. Behind the guard a run "
            + "started while another cockpit holds the claim stands down without a word: exit code 0, no output "
            + "and no file, which is exactly the failure AC-1235 was reported for.");
    }
}
