namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// The counting rule behind the AC-517 badge: `PullRequestBadgeCounts.Compute` mirrors exactly what
// the old always-visible section computed for its "N open · M waiting on you" line, so the badge is provably the
// same truth rather than a second one that can drift from the dialog.
public class PullRequestBadgeCountsTests
{
    private static readonly GitHubPullRequest Mine = new(1, "Faster startup", "https://github.com/o/r/pull/1", null, "o/r", "me");
    private static readonly GitHubPullRequest AlsoMine = new(2, "Fix the sidebar", "https://github.com/o/r/pull/2", null, "o/r", "me");
    private static readonly GitHubPullRequest FromAnotherRepo = new(3, "Bump deps", "https://github.com/o/other", null, "o/other", "me");

    [Fact]
    public void NothingOpen_BothCountsAreZero()
    {
        var result = new PullRequestFeedResult([], [], RepositoryMissing: false);

        var (primary, secondary) = PullRequestBadgeCounts.Compute(result, EmptySet(), EmptySet());

        Assert.Equal(0, primary);
        Assert.Equal(0, secondary);
    }

    [Fact]
    public void OpenPullRequests_CountTowardsPrimary_RegardlessOfReviewStatus()
    {
        var result = new PullRequestFeedResult([Mine, AlsoMine], [], RepositoryMissing: false);

        var (primary, secondary) = PullRequestBadgeCounts.Compute(result, EmptySet(), EmptySet());

        Assert.Equal(2, primary);
        Assert.Equal(0, secondary);
    }

    [Fact]
    public void APullRequestWaitingOnReview_CountsInBothPrimaryAndSecondary()
    {
        var result = new PullRequestFeedResult([Mine, AlsoMine], [Mine], RepositoryMissing: false);

        var (primary, secondary) = PullRequestBadgeCounts.Compute(result, EmptySet(), EmptySet());

        Assert.Equal(2, primary);
        Assert.Equal(1, secondary);
    }

    [Fact]
    public void AnIgnoredPullRequest_CountsInNeitherCounter_EvenWhileWaitingOnReview()
    {
        var result = new PullRequestFeedResult([Mine, AlsoMine], [Mine], RepositoryMissing: false);
        var ignored = new HashSet<string>(StringComparer.Ordinal) { Mine.Url };

        var (primary, secondary) = PullRequestBadgeCounts.Compute(result, ignored, EmptySet());

        Assert.Equal(1, primary);
        Assert.Equal(0, secondary);
    }

    [Fact]
    public void AnIgnoredRepository_TakesOutEveryPullRequestFromIt()
    {
        var result = new PullRequestFeedResult([Mine, FromAnotherRepo], [], RepositoryMissing: false);
        var ignoredRepositories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FromAnotherRepo.Repository };

        var (primary, _) = PullRequestBadgeCounts.Compute(result, EmptySet(), ignoredRepositories);

        Assert.Equal(1, primary);
    }

    [Fact]
    public void AReviewRequestOutsideThePullRequestsList_DoesNotInflateSecondary()
    {
        // ReviewRequested is not itself repository-filtered (PullRequestFeed.cs) — a request for a pull request
        // that the repo filter dropped from PullRequests must not still count towards the badge.
        var filteredOut = new GitHubPullRequest(9, "Elsewhere", "https://github.com/o/elsewhere/pull/9", null, "o/elsewhere", "me");
        var result = new PullRequestFeedResult([Mine], [Mine, filteredOut], RepositoryMissing: false);

        var (primary, secondary) = PullRequestBadgeCounts.Compute(result, EmptySet(), EmptySet());

        Assert.Equal(1, primary);
        Assert.Equal(1, secondary);
    }

    private static HashSet<string> EmptySet() => new(StringComparer.Ordinal);
}
