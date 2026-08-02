namespace Cockpit.Plugin.GitHubIssues.Tests;

// The branch name an issue hands to the rest of the flow (#77). It ends up in a git command, so anything that a ref
// or a shell would argue with has to be gone before it leaves here — and a title is written by a human, which means
// it will contain a colon, a slash and an emoji sooner or later.
public class GitHubBranchNameTests
{
    [Fact]
    public void ANumberAndATitle_BecomeALowercaseSlug() =>
        Assert.Equal("42-fix-the-login-redirect", GitHubBranchName.From(42, "Fix the login redirect"));

    [Theory]
    [InlineData("Fix: the login/redirect!", "42-fix-the-login-redirect")]
    [InlineData("  Spaces   everywhere  ", "42-spaces-everywhere")]
    [InlineData("Emoji 🎉 and ümlauts", "42-emoji-and-umlauts")]
    public void PunctuationAndPadding_NeverReachTheRef(string title, string expected) =>
        Assert.Equal(expected, GitHubBranchName.From(42, title));

    [Fact]
    public void ATitleThatIsAnEssay_IsCut_AndDoesNotEndInADash()
    {
        var branchName = GitHubBranchName.From(42, new string('a', 20) + " " + new string('b', 80));

        Assert.Equal(63, branchName.Length);
        Assert.False(branchName.EndsWith("-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!!!")]
    public void AnIssueWithNothingSayableInItsTitle_IsStillABranch(string? title) =>
        Assert.Equal("42", GitHubBranchName.From(42, title));

    [Fact]
    public void APatternIsFollowed_BecauseTheConventionIsTheTeamsToChoose() =>
        Assert.Equal("feature/42", GitHubBranchName.From(42, "Fix the login redirect", "feature/{number}"));

    [Fact]
    public void AnIssueWithNothingSayable_LeavesNoDanglingSeparator() =>
        Assert.Equal("42", GitHubBranchName.From(42, "!!!", "{number}-{title}"));

    [Fact]
    public void NoPattern_IsTheDefaultOne() =>
        Assert.Equal(GitHubBranchName.From(42, "Fix the login redirect", GitHubBranchName.DefaultPattern), GitHubBranchName.From(42, "Fix the login redirect"));
}
