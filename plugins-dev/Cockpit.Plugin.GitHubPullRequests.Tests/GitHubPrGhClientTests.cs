using Cockpit.TestSupport;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

/// <summary>
/// The gh query behind the review-requested list (#74). Asserted here rather than by shelling out: a wrong
/// filter would silently list the wrong pull requests, and <c>--review-requested @me</c> is the whole feature.
/// </summary>
public class GitHubPrGhClientTests
{
    [Fact]
    public void ReviewRequestedArguments_SearchOpenPullRequestsAwaitingMyReview()
    {
        var arguments = GitHubPrGhClient.ReviewRequestedArguments;

        Assert.True(SequenceAssert.ContainsInOrder(arguments, "search", "prs"));
        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--review-requested", "@me"));
        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--state", "open"));
        // updatedAt is asked for because the list is ordered by it: the pull request somebody just touched is the
        // one worth looking at first, and without the field there is nothing to sort on.
        Assert.Contains("number,title,url,body,repository,author,updatedAt", arguments);
    }
}
