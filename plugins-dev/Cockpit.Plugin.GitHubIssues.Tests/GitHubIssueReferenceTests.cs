namespace Cockpit.Plugin.GitHubIssues.Tests;

// Which issue a flow means (#77). The dangerous case is the last one: a bare number with no repository names an
// issue in a repository nobody stated — and commenting on the wrong repo's #42 is not a mistake that announces
// itself.
public class GitHubIssueReferenceTests
{
    [Fact]
    public void ANumberWithARepository_IsThatIssue() =>
        Assert.Equal(new GitHubIssueReference("raymondkrahwinkel/AI-Cockpit", 42), GitHubIssueReference.Parse("42", "raymondkrahwinkel/AI-Cockpit"));

    [Fact]
    public void AHashInFrontOfIt_IsHowPeopleWriteIt() =>
        Assert.Equal(42, GitHubIssueReference.Parse("#42", "raymondkrahwinkel/AI-Cockpit").Number);

    [Fact]
    public void AQualifiedIssue_CarriesItsOwnRepository() =>
        Assert.Equal(new GitHubIssueReference("raymondkrahwinkel/AI-Cockpit", 42), GitHubIssueReference.Parse("raymondkrahwinkel/AI-Cockpit#42", string.Empty));

    [Fact]
    public void TheUrlYouCopiedFromTheBrowser_Works_BecauseThatIsWhatPeopleActuallyPaste() =>
        Assert.Equal(new GitHubIssueReference("raymondkrahwinkel/AI-Cockpit", 42), GitHubIssueReference.Parse("https://github.com/raymondkrahwinkel/AI-Cockpit/issues/42", string.Empty));

    [Fact]
    public void ABareNumberWithNoRepository_IsRefused_RatherThanGuessedAt()
    {
        var parse = () => GitHubIssueReference.Parse("42", string.Empty);

        var ex = Assert.Throws<InvalidOperationException>(parse);
        Assert.Contains("which repository", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("the login one")]
    public void SomethingThatIsNotAnIssue_SaysWhatItAccepts(string written)
    {
        var parse = () => GitHubIssueReference.Parse(written, "raymondkrahwinkel/AI-Cockpit");

        var ex = Assert.Throws<InvalidOperationException>(parse);
        Assert.Contains("owner/repo#number", ex.Message, StringComparison.Ordinal);
    }
}
