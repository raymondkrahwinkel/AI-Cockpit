using FluentAssertions;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

/// <summary>
/// <see cref="ReviewRequestInbox"/> (#74): which review requests count as new, and what the next look should
/// remember — the rule behind "announce this one, but only once".
/// </summary>
public class ReviewRequestInboxTests
{
    [Fact]
    public void Reconcile_WithNothingSeenYet_TreatsEveryRequestAsArrived()
    {
        var reviewRequested = new[] { _PullRequest(1, "acme/api"), _PullRequest(2, "acme/web") };

        var inbox = ReviewRequestInbox.Reconcile(reviewRequested, new HashSet<string>());

        inbox.Arrived.Should().BeEquivalentTo(reviewRequested);
        inbox.Seen.Should().BeEquivalentTo(["acme/api#1", "acme/web#2"]);
    }

    [Fact]
    public void Reconcile_WithAnAlreadySeenRequest_AnnouncesOnlyTheNewOne()
    {
        var seenOne = _PullRequest(1, "acme/api");
        var newOne = _PullRequest(2, "acme/web");

        var inbox = ReviewRequestInbox.Reconcile([seenOne, newOne], new HashSet<string> { "acme/api#1" });

        inbox.Arrived.Should().Equal(newOne);
    }

    [Fact]
    public void Reconcile_WithTheSameNumberInAnotherRepository_AnnouncesIt()
    {
        var otherRepository = _PullRequest(1, "acme/web");

        var inbox = ReviewRequestInbox.Reconcile([otherRepository], new HashSet<string> { "acme/api#1" });

        inbox.Arrived.Should().Equal(otherRepository);
    }

    [Fact]
    public void Reconcile_WithNoOpenRequests_ForgetsTheOnesThatClosed()
    {
        var inbox = ReviewRequestInbox.Reconcile([], new HashSet<string> { "acme/api#1" });

        inbox.Arrived.Should().BeEmpty();
        inbox.Seen.Should().BeEmpty();
    }

    [Fact]
    public void Reconcile_WithAClosedRequestThatIsAskedAgain_AnnouncesItAgain()
    {
        var pullRequest = _PullRequest(1, "acme/api");

        var afterItClosed = ReviewRequestInbox.Reconcile([], new HashSet<string> { "acme/api#1" });
        var afterItReturned = ReviewRequestInbox.Reconcile([pullRequest], afterItClosed.Seen);

        afterItReturned.Arrived.Should().Equal(pullRequest);
    }

    private static GitHubPullRequest _PullRequest(int number, string repository) =>
        new(number, $"Pull request {number}", $"https://github.com/{repository}/pull/{number}", Body: null, repository, Author: "someone");
}
