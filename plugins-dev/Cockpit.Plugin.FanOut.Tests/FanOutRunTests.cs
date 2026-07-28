namespace Cockpit.Plugin.FanOut.Tests;

public class FanOutRunTests
{
    private const string Task = "Speed up the importer.";
    private const string Repository = @"C:\repos\importer";

    [Fact]
    public void ToRequests_OneProfileSeveralAngles_AsksForOneSessionPerAngle()
    {
        var run = _Run(
            new FanOutVariant("Personal", "the smallest change that works"),
            new FanOutVariant("Personal", "rewrite the hot path"),
            new FanOutVariant("Personal", "cache instead of compute"));

        var requests = run.ToRequests("run-1");

        Assert.Equal(3, requests.Count);
        Assert.All(requests, request => Assert.Equal("Personal", request.ProfileId));
        Assert.Equal(3, requests.Select(request => request.InitialUserMessage).Distinct().Count());
        Assert.All(requests, request => Assert.Contains(Task, request.InitialUserMessage ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void ToRequests_SeveralProfilesOneBrief_AsksEachProfileTheSameThing()
    {
        var run = _Run(
            new FanOutVariant("Personal", string.Empty),
            new FanOutVariant("Codex", string.Empty),
            new FanOutVariant("Local", string.Empty));

        var requests = run.ToRequests("run-2");

        Assert.Equal(["Personal", "Codex", "Local"], requests.Select(request => request.ProfileId));
        Assert.Single(requests.Select(request => request.InitialUserMessage).Distinct());
    }

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

    [Fact]
    public void ToRequests_AnyRun_RecordsEveryArmAgainstTheOneRun()
    {
        var requests = _Run(new FanOutVariant("Personal", "a"), new FanOutVariant("Personal", "b")).ToRequests("run-4");

        Assert.All(requests, request =>
        {
            Assert.Equal("run-4", request.RunId);
            Assert.Equal("Speed up the importer.", request.RunLabel);
        });
    }

    [Fact]
    public void ToRequests_NoProfileChosen_LeavesTheHostToStartItsDefault()
    {
        var requests = _Run(new FanOutVariant(string.Empty, "a"), new FanOutVariant("  ", "b")).ToRequests("run-5");

        Assert.All(requests, request => Assert.Null(request.ProfileId));
    }

    [Fact]
    public void ToRequests_NoRepositoryGiven_LeavesTheHostToUseItsOwnDirectory()
    {
        var run = new FanOutRun(Task, "  ", [new FanOutVariant("Personal", "a"), new FanOutVariant("Personal", "b")]);

        Assert.All(run.ToRequests("run-6"), request => Assert.Null(request.WorkingDirectory));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void CanStart_ArmCount_HoldsTheTwoToFiveRange(int arms, bool expected)
    {
        var run = new FanOutRun(Task, Repository, Enumerable.Range(0, arms).Select(index => new FanOutVariant("Personal", $"angle {index}")).ToList());

        Assert.Equal(expected, run.CanStart);
    }

    [Fact]
    public void CanStart_NoTaskTyped_IsFalseHoweverManyArmsAreSetUp()
    {
        var run = new FanOutRun("   ", Repository, [new FanOutVariant("Personal", "a"), new FanOutVariant("Codex", "b")]);

        Assert.False(run.CanStart);
    }

    private static FanOutRun _Run(params FanOutVariant[] variants) => new(Task, Repository, variants);
}
