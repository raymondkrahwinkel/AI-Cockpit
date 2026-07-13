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
}
