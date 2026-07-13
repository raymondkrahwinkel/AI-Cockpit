using FluentAssertions;

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

        arguments.Should().ContainInOrder("search", "prs");
        arguments.Should().ContainInOrder("--review-requested", "@me");
        arguments.Should().ContainInOrder("--state", "open");
        arguments.Should().Contain("number,title,url,body,repository,author");
    }
}
