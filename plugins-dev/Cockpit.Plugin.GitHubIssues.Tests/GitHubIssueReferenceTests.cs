namespace Cockpit.Plugin.GitHubIssues.Tests;

// Which issue a flow means (#77). The dangerous case is the last one: a bare number with no repository names an
// issue in a repository nobody stated — and commenting on the wrong repo's #42 is not a mistake that announces
// itself.
public class GitHubIssueReferenceTests
{
    [Fact]
    public void TheUrlYouCopiedFromTheBrowser_Works_BecauseThatIsWhatPeopleActuallyPaste() =>
        Assert.Equal(new GitHubIssueReference("raymondkrahwinkel/AI-Cockpit", 42), GitHubIssueReference.Parse("https://github.com/raymondkrahwinkel/AI-Cockpit/issues/42", string.Empty));
}
