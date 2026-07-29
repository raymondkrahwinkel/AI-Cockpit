namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// <see cref="BranchName"/> (#75): the branch a ticket is started on — <c>[issue-id]-[short-name]</c>, all
/// lowercase (the convention this repo follows), and safe to type, to push and to read back six months later.
/// </summary>
public class BranchNameTests
{
    [Fact]
    public void From_LowercasesAndJoinsTheSummaryWithHyphens()
    {
        Assert.Equal("web-42-fix-the-basket-total", BranchName.From("WEB-42", "Fix the basket total"));
    }

    [Fact]
    public void From_DropsPunctuationRatherThanPushingItIntoAGitRef()
    {
        Assert.Equal("web-7-don-t-crash-on-empty-carts", BranchName.From("WEB-7", "Don't crash on empty carts!"));
    }

    [Fact]
    public void From_FoldsAccentsToTheirBaseLetter()
    {
        Assert.Equal("web-9-naive-cafe", BranchName.From("WEB-9", "Naïve café"));
    }

    [Fact]
    public void From_CutsALongSummaryOnAWordBoundary()
    {
        var name = BranchName.From("WEB-1", "Refactor the entire importer pipeline so that it stops timing out");

        Assert.StartsWith("web-1-refactor-the-entire-importer-pipeline", name);
        Assert.False(name.EndsWith('-'));
        Assert.NotEqual("pipelin", name.Split('-').Last());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void From_WithNothingUsableInTheSummary_FallsBackToTheIssueId(string? summary)
    {
        Assert.Equal("web-3", BranchName.From("WEB-3", summary));
    }

    [Fact]
    public void APatternIsFollowed_BecauseTheConventionIsTheTeamsAndNotThePluginsToChoose() =>
        Assert.Equal("feature/web-14", BranchName.From("WEB-14", "Fix the login redirect", "feature/{id}"));

    [Theory]
    [InlineData("{id}_{summary}", "web-14_fix-the-login-redirect")]
    [InlineData("{summary}", "fix-the-login-redirect")]
    [InlineData("bugfix/{id}-{summary}", "bugfix/web-14-fix-the-login-redirect")]
    public void EveryPlaceholderIsFilled_AndTheResultIsStillARefGitAccepts(string pattern, string expected) =>
        Assert.Equal(expected, BranchName.From("WEB-14", "Fix the login redirect", pattern));

    [Fact]
    public void AnIssueWithNoSummary_LeavesNoDanglingSeparator() =>
        // "WEB-14-" is a name someone typed wrong, and it looks like one.
        Assert.Equal("web-14", BranchName.From("WEB-14", null, "{id}-{summary}"));

    [Fact]
    public void APatternThatSaysNothing_FallsBackToTheId() =>
        Assert.Equal("web-14", BranchName.From("WEB-14", "Fix it", "///"));

    [Fact]
    public void NoPattern_IsTheDefaultOne() =>
        Assert.Equal(BranchName.From("WEB-14", "Fix the login redirect", BranchName.DefaultPattern), BranchName.From("WEB-14", "Fix the login redirect"));
}
