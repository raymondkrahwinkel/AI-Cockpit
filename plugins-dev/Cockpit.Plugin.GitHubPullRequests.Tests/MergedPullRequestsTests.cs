using FluentAssertions;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

/// <summary>
/// Turning a poll into a change (#69). GitHub cannot tell a desktop app that something was merged, so it is asked —
/// and an answer is the world, not the news in it.
/// <para>
/// The rule that matters is the first look. Every pull request the operator has ever merged is "new" to a process that
/// has just started, and a flow that ran forty times the moment the cockpit opened would be the last time anyone armed
/// it. So the first look remembers and fires nothing.
/// </para>
/// </summary>
public class MergedPullRequestsTests
{
    [Fact]
    public void TheFirstLook_FiresNothing_AndRemembersEverything()
    {
        var result = MergedPullRequests.Reconcile([_Pr(1), _Pr(2)], new HashSet<string>(), primed: false);

        result.Merged.Should().BeEmpty("everything already merged is not news");
        result.Seen.Should().HaveCount(2, "but it is all remembered, or it would be news next time instead");
    }

    [Fact]
    public void APullRequestMergedSinceTheLastLook_IsTheNews()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { MergedPullRequests.KeyOf(_Pr(1)) };

        var result = MergedPullRequests.Reconcile([_Pr(1), _Pr(2)], seen, primed: true);

        result.Merged.Should().ContainSingle().Which.Number.Should().Be(2);
    }

    [Fact]
    public void NothingNew_FiresNothing()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { MergedPullRequests.KeyOf(_Pr(1)) };

        MergedPullRequests.Reconcile([_Pr(1)], seen, primed: true).Merged.Should().BeEmpty();
    }

    [Fact]
    public void APullRequestThatFallsOutOfTheSearchWindow_IsNotForgotten() =>
        // gh returns the last thirty. A merge that scrolls off the end must not become "new" again the day someone
        // reverts and re-merges something else — what has been seen stays seen.
        MergedPullRequests
            .Reconcile([_Pr(9)], new HashSet<string>(StringComparer.Ordinal) { "raymondkrahwinkel/AI-Cockpit#1" }, primed: true)
            .Seen.Should().Contain("raymondkrahwinkel/AI-Cockpit#1");

    [Fact]
    public void TheSameNumberInAnotherRepository_IsAnotherPullRequest() =>
        MergedPullRequests.KeyOf(_Pr(1)).Should().NotBe(MergedPullRequests.KeyOf(_Pr(1, "raymondkrahwinkel/Eveworkbench")));

    [Fact]
    public void TheQueryAsksForYourOwnMergedPullRequests() =>
        // A flow that fired on every merge in every repository the operator can see would be a flow about other
        // people's afternoons.
        GitHubPrGhClient.MergedArguments.Should().ContainInOrder("--author", "@me", "--merged");

    private static GitHubPullRequest _Pr(int number, string repository = "raymondkrahwinkel/AI-Cockpit") =>
        new(number, $"Pull request {number}", $"https://github.com/{repository}/pull/{number}", null, repository, "raymondkrahwinkel");
}
