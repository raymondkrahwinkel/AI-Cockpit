using Cockpit.TestSupport;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// Turning a poll into a change (#69). GitHub cannot tell a desktop app that something was merged, so it is asked —
// and an answer is the world, not the news in it.
//
// The rule that matters is the first look. Every pull request the operator has ever merged is "new" to a process that
// has just started, and a flow that ran forty times the moment the cockpit opened would be the last time anyone armed
// it. So the first look remembers and fires nothing.
public class MergedPullRequestsTests
{
    [Fact]
    public void TheFirstLook_FiresNothing_AndRemembersEverything()
    {
        var result = MergedPullRequests.Reconcile([_Pr(1), _Pr(2)], new HashSet<string>(), primed: false);

        Assert.Empty(result.Merged);
        Assert.Equal(2, System.Linq.Enumerable.Count(result.Seen));
    }

    [Fact]
    public void APullRequestMergedSinceTheLastLook_IsTheNews()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { MergedPullRequests.KeyOf(_Pr(1)) };

        var result = MergedPullRequests.Reconcile([_Pr(1), _Pr(2)], seen, primed: true);

        Assert.Equal(2, Assert.Single(result.Merged).Number);
    }

    [Fact]
    public void NothingNew_FiresNothing()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { MergedPullRequests.KeyOf(_Pr(1)) };

        Assert.Empty(MergedPullRequests.Reconcile([_Pr(1)], seen, primed: true).Merged);
    }

    [Fact]
    public void APullRequestThatFallsOutOfTheSearchWindow_IsNotForgotten() =>
        // gh returns the last thirty. A merge that scrolls off the end must not become "new" again the day someone
        // reverts and re-merges something else — what has been seen stays seen.
        Assert.Contains("raymondkrahwinkel/AI-Cockpit#1", MergedPullRequests
            .Reconcile([_Pr(9)], new HashSet<string>(StringComparer.Ordinal) { "raymondkrahwinkel/AI-Cockpit#1" }, primed: true)
            .Seen);

    [Fact]
    public void TheSameNumberInAnotherRepository_IsAnotherPullRequest() =>
        Assert.NotEqual(MergedPullRequests.KeyOf(_Pr(1, "acme/webshop")), MergedPullRequests.KeyOf(_Pr(1)));

    [Fact]
    public void TheQueryAsksForYourOwnMergedPullRequests() =>
        // A flow that fired on every merge in every repository the operator can see would be a flow about other
        // people's afternoons.
        Assert.True(SequenceAssert.ContainsInOrder(GitHubPrGhClient.MergedArguments, "--author", "@me", "--merged"));

    private static GitHubPullRequest _Pr(int number, string repository = "raymondkrahwinkel/AI-Cockpit") =>
        new(number, $"Pull request {number}", $"https://github.com/{repository}/pull/{number}", null, repository, "raymondkrahwinkel");
}
