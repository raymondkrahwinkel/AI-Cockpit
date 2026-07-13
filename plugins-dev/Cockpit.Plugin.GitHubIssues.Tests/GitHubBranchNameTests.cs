using FluentAssertions;

namespace Cockpit.Plugin.GitHubIssues.Tests;

/// <summary>
/// The branch name an issue hands to the rest of the flow (#77). It ends up in a git command, so anything that a ref
/// or a shell would argue with has to be gone before it leaves here — and a title is written by a human, which means
/// it will contain a colon, a slash and an emoji sooner or later.
/// </summary>
public class GitHubBranchNameTests
{
    [Fact]
    public void ANumberAndATitle_BecomeALowercaseSlug() =>
        GitHubBranchName.From(42, "Fix the login redirect").Should().Be("42-fix-the-login-redirect");

    [Theory]
    [InlineData("Fix: the login/redirect!", "42-fix-the-login-redirect")]
    [InlineData("  Spaces   everywhere  ", "42-spaces-everywhere")]
    [InlineData("Emoji 🎉 and ümlauts", "42-emoji-and-umlauts")]
    public void PunctuationAndPadding_NeverReachTheRef(string title, string expected) =>
        GitHubBranchName.From(42, title).Should().Be(expected);

    [Fact]
    public void ATitleThatIsAnEssay_IsCut_AndDoesNotEndInADash() =>
        GitHubBranchName.From(42, new string('a', 20) + " " + new string('b', 80))
            .Should().HaveLength(63).And.NotEndWith("-");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!!!")]
    public void AnIssueWithNothingSayableInItsTitle_IsStillABranch(string? title) =>
        GitHubBranchName.From(42, title).Should().Be("42");

    [Fact]
    public void APatternIsFollowed_BecauseTheConventionIsTheTeamsToChoose() =>
        GitHubBranchName.From(42, "Fix the login redirect", "feature/{number}")
            .Should().Be("feature/42");

    [Fact]
    public void AnIssueWithNothingSayable_LeavesNoDanglingSeparator() =>
        GitHubBranchName.From(42, "!!!", "{number}-{title}").Should().Be("42");

    [Fact]
    public void NoPattern_IsTheDefaultOne() =>
        GitHubBranchName.From(42, "Fix the login redirect")
            .Should().Be(GitHubBranchName.From(42, "Fix the login redirect", GitHubBranchName.DefaultPattern));
}
