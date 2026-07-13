using FluentAssertions;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// <see cref="BranchName"/> (#75): the branch a ticket is started on — <c>[issue-id]-[short-name]</c>, all
/// lowercase (Raymond's own convention), and safe to type, to push and to read back six months later.
/// </summary>
public class BranchNameTests
{
    [Fact]
    public void From_LowercasesAndJoinsTheSummaryWithHyphens()
    {
        BranchName.From("EWB-42", "Fix the market cache").Should().Be("ewb-42-fix-the-market-cache");
    }

    [Fact]
    public void From_DropsPunctuationRatherThanPushingItIntoAGitRef()
    {
        BranchName.From("EWB-7", "Don't crash on empty fits!").Should().Be("ewb-7-don-t-crash-on-empty-fits");
    }

    [Fact]
    public void From_FoldsAccentsToTheirBaseLetter()
    {
        BranchName.From("EWB-9", "Naïve café").Should().Be("ewb-9-naive-cafe");
    }

    [Fact]
    public void From_CutsALongSummaryOnAWordBoundary()
    {
        var name = BranchName.From("EWB-1", "Refactor the entire importer pipeline so that it stops timing out");

        name.Should().StartWith("ewb-1-refactor-the-entire-importer-pipeline");
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
        BranchName.From("EWB-3", summary).Should().Be("ewb-3");
    }

    [Fact]
    public void APatternIsFollowed_BecauseTheConventionIsTheTeamsAndNotThePluginsToChoose() =>
        BranchName.From("EVE-14", "Fix the login redirect", "feature/{id}")
            .Should().Be("feature/eve-14");

    [Theory]
    [InlineData("{id}_{summary}", "eve-14_fix-the-login-redirect")]
    [InlineData("{summary}", "fix-the-login-redirect")]
    [InlineData("bugfix/{id}-{summary}", "bugfix/eve-14-fix-the-login-redirect")]
    public void EveryPlaceholderIsFilled_AndTheResultIsStillARefGitAccepts(string pattern, string expected) =>
        BranchName.From("EVE-14", "Fix the login redirect", pattern).Should().Be(expected);

    [Fact]
    public void AnIssueWithNoSummary_LeavesNoDanglingSeparator() =>
        // "EVE-14-" is a name someone typed wrong, and it looks like one.
        BranchName.From("EVE-14", null, "{id}-{summary}").Should().Be("eve-14");

    [Fact]
    public void APatternThatSaysNothing_FallsBackToTheId() =>
        BranchName.From("EVE-14", "Fix it", "///").Should().Be("eve-14");

    [Fact]
    public void NoPattern_IsTheDefaultOne() =>
        BranchName.From("EVE-14", "Fix the login redirect")
            .Should().Be(BranchName.From("EVE-14", "Fix the login redirect", BranchName.DefaultPattern));
}
