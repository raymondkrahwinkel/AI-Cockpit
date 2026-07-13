using FluentAssertions;

namespace Cockpit.Plugin.GitHubIssues.Tests;

/// <summary>
/// Which issue a flow means (#77). The dangerous case is the last one: a bare number with no repository names an
/// issue in a repository nobody stated — and commenting on the wrong repo's #42 is not a mistake that announces
/// itself.
/// </summary>
public class GitHubIssueReferenceTests
{
    [Fact]
    public void ANumberWithARepository_IsThatIssue() =>
        GitHubIssueReference.Parse("42", "raymondkrahwinkel/AI-Cockpit")
            .Should().Be(new GitHubIssueReference("raymondkrahwinkel/AI-Cockpit", 42));

    [Fact]
    public void AHashInFrontOfIt_IsHowPeopleWriteIt() =>
        GitHubIssueReference.Parse("#42", "raymondkrahwinkel/AI-Cockpit").Number.Should().Be(42);

    [Fact]
    public void AQualifiedIssue_CarriesItsOwnRepository() =>
        GitHubIssueReference.Parse("raymondkrahwinkel/AI-Cockpit#42", string.Empty)
            .Should().Be(new GitHubIssueReference("raymondkrahwinkel/AI-Cockpit", 42));

    [Fact]
    public void TheUrlYouCopiedFromTheBrowser_Works_BecauseThatIsWhatPeopleActuallyPaste() =>
        GitHubIssueReference.Parse("https://github.com/raymondkrahwinkel/AI-Cockpit/issues/42", string.Empty)
            .Should().Be(new GitHubIssueReference("raymondkrahwinkel/AI-Cockpit", 42));

    [Fact]
    public void ABareNumberWithNoRepository_IsRefused_RatherThanGuessedAt()
    {
        var parse = () => GitHubIssueReference.Parse("42", string.Empty);

        parse.Should().Throw<InvalidOperationException>().WithMessage("*which repository*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("the login one")]
    public void SomethingThatIsNotAnIssue_SaysWhatItAccepts(string written)
    {
        var parse = () => GitHubIssueReference.Parse(written, "raymondkrahwinkel/AI-Cockpit");

        parse.Should().Throw<InvalidOperationException>().WithMessage("*owner/repo#number*");
    }
}
