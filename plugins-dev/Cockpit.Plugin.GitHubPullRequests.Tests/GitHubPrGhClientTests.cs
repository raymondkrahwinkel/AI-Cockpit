using Cockpit.TestSupport;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// The gh query behind the review-requested list (#74). Asserted here rather than by shelling out: a wrong
// filter would silently list the wrong pull requests, and `--review-requested @me` is the whole feature.
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

    // AC-818: the by-number status lookup behind get_pr_status.
    [Fact]
    public void ParsePullRequestStatus_ReadsTitleUrlMergeableReviewDecisionAndChecks()
    {
        const string json = """
            { "number": 620, "title": "AC-818: PR status cache", "url": "https://github.com/o/r/pull/620",
              "mergeable": "MERGEABLE", "reviewDecision": "APPROVED",
              "statusCheckRollup": [
                { "__typename": "CheckRun", "name": "build", "status": "COMPLETED", "conclusion": "SUCCESS" }
              ]
            }
            """;

        var status = GitHubPrGhClient.ParsePullRequestStatus(json);

        Assert.NotNull(status);
        Assert.Equal(620, status!.Number);
        Assert.Equal("AC-818: PR status cache", status.Title);
        Assert.Equal("https://github.com/o/r/pull/620", status.Url);
        Assert.Equal("MERGEABLE", status.Mergeable);
        Assert.Equal("APPROVED", status.ReviewDecision);
        Assert.Single(status.Checks);
        Assert.Equal("build", status.Checks[0].Name);
        Assert.Equal(PullRequestCheckState.Passed, status.Checks[0].State);
    }

    [Fact]
    public void ParsePullRequestStatus_NoReviewDecision_IsNull()
    {
        const string json = """
            { "number": 1, "title": "x", "url": "https://github.com/o/r/pull/1", "mergeable": "UNKNOWN", "statusCheckRollup": [] }
            """;

        var status = GitHubPrGhClient.ParsePullRequestStatus(json);

        Assert.Null(status!.ReviewDecision);
        Assert.Equal("UNKNOWN", status.Mergeable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("null")]
    public void ParsePullRequestStatus_ToleratesEmptyOrInvalidJson(string json)
    {
        Assert.Null(GitHubPrGhClient.ParsePullRequestStatus(json));
    }
}
