using FluentAssertions;

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
        BranchName.From("WEB-42", "Fix the basket total").Should().Be("web-42-fix-the-basket-total");
    }

    [Fact]
    public void From_DropsPunctuationRatherThanPushingItIntoAGitRef()
    {
        BranchName.From("WEB-7", "Don't crash on empty carts!").Should().Be("web-7-don-t-crash-on-empty-carts");
    }

    [Fact]
    public void From_FoldsAccentsToTheirBaseLetter()
    {
        BranchName.From("WEB-9", "Naïve café").Should().Be("web-9-naive-cafe");
    }

    [Fact]
    public void From_CutsALongSummaryOnAWordBoundary()
    {
        var name = BranchName.From("WEB-1", "Refactor the entire importer pipeline so that it stops timing out");

        name.Should().StartWith("web-1-refactor-the-entire-importer-pipeline");
        name.Should().NotEndWith("-");
        name.Split('-').Last().Should().NotBe("pipelin");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void From_WithNothingUsableInTheSummary_FallsBackToTheIssueId(string? summary)
    {
        BranchName.From("WEB-3", summary).Should().Be("web-3");
    }

    [Fact]
    public void APatternIsFollowed_BecauseTheConventionIsTheTeamsAndNotThePluginsToChoose() =>
        BranchName.From("WEB-14", "Fix the login redirect", "feature/{id}")
            .Should().Be("feature/web-14");

    [Theory]
    [InlineData("{id}_{summary}", "web-14_fix-the-login-redirect")]
    [InlineData("{summary}", "fix-the-login-redirect")]
    [InlineData("bugfix/{id}-{summary}", "bugfix/web-14-fix-the-login-redirect")]
    public void EveryPlaceholderIsFilled_AndTheResultIsStillARefGitAccepts(string pattern, string expected) =>
        BranchName.From("WEB-14", "Fix the login redirect", pattern).Should().Be(expected);

    [Fact]
    public void AnIssueWithNoSummary_LeavesNoDanglingSeparator() =>
        // "WEB-14-" is a name someone typed wrong, and it looks like one.
        BranchName.From("WEB-14", null, "{id}-{summary}").Should().Be("web-14");

    [Fact]
    public void APatternThatSaysNothing_FallsBackToTheId() =>
        BranchName.From("WEB-14", "Fix it", "///").Should().Be("web-14");

    [Fact]
    public void NoPattern_IsTheDefaultOne() =>
        BranchName.From("WEB-14", "Fix the login redirect")
            .Should().Be(BranchName.From("WEB-14", "Fix the login redirect", BranchName.DefaultPattern));
}
