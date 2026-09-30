namespace Cockpit.Plugin.FanOut.Tests;

public class FanOutRunTests
{
    private const string Task = "Speed up the importer.";
    private const string Repository = @"C:\repos\importer";

    [Fact]
    public void ToRequests_AnyRun_IsolatesEveryArmInItsOwnWorktree()
    {
        var requests = _Run(new FanOutVariant("Personal", "a"), new FanOutVariant("Personal", "b")).ToRequests("run-3");

        Assert.All(requests, request =>
        {
            Assert.True(request.IsolateInWorktree);
            // Left unset on purpose: a path here would put every arm in one shared worktree, which is what the
            // isolation is for avoiding.
            Assert.Null(request.WorktreePath);
            Assert.Equal(Repository, request.WorkingDirectory);
        });
    }

    private static FanOutRun _Run(params FanOutVariant[] variants) => new(Task, Repository, variants);
}
