using Cockpit.TestSupport;

namespace Cockpit.Plugin.GitHubIssues.Tests;

/// <summary>
/// The <c>gh</c> arguments this plugin builds (AC-519), asserted the same way <c>GitHubPrGhClientTests</c> asserts
/// the pull-requests plugin's: without shelling out to a real <c>gh</c>. The real process path — a fake <c>gh</c> on
/// PATH driving the actual <see cref="GitHubGhClient"/>, including failure and multi-repo scenarios — was measured
/// separately in a disposable scratchpad harness; committed here is the query construction this repo keeps testing
/// without a live process.
/// </summary>
public class GitHubGhClientTests
{
    [Fact]
    public void SearchArguments_UsesTheDocumentedPageLimit_NotARoundNumberAboveIt()
    {
        // AC-519: the dialog's truncation warning fires on exactly this count, so the argument that requests it and
        // the constant the warning checks against must be the same number — asserted here against the constant
        // itself, not a hardcoded "100" that could silently drift out of step with it.
        var arguments = GitHubGhClient.SearchArguments("octocat", assignedToMe: false, extraTerms: null);

        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--limit", GitHubGhClient.IssueSearchLimit.ToString()));
    }

    [Fact]
    public void SearchArguments_ScopesToTheOwner_OpenIssuesOnly()
    {
        var arguments = GitHubGhClient.SearchArguments("octocat", assignedToMe: false, extraTerms: null);

        Assert.True(SequenceAssert.ContainsInOrder(arguments, "search", "issues"));
        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--owner", "octocat"));
        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--state", "open"));
    }

    [Fact]
    public void SearchArguments_AssignedToMe_AddsTheAssigneeFlag()
    {
        var arguments = GitHubGhClient.SearchArguments("octocat", assignedToMe: true, extraTerms: null);

        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--assignee", "@me"));
    }

    [Fact]
    public void SearchArguments_WithoutExtraTerms_CarriesNoStrayArgument()
    {
        var withTerms = GitHubGhClient.SearchArguments("octocat", assignedToMe: false, extraTerms: "label:bug");
        var withoutTerms = GitHubGhClient.SearchArguments("octocat", assignedToMe: false, extraTerms: null);

        Assert.Equal(withTerms.Length - 1, withoutTerms.Length);
    }

    [Fact]
    public void SearchArguments_WithALabelSearchTerm_CarriesItAsItsOwnArgument()
    {
        // AC-519 (server-side label filtering): this is what the dialog hands _gh.SearchOpenIssuesAsync's extraTerms
        // when a label is selected — it has to survive as one argument, not get merged away.
        var arguments = GitHubGhClient.SearchArguments("octocat", assignedToMe: false, GitHubGhClient.LabelSearchTerm("bug"));

        Assert.Contains("label:\"bug\"", arguments);
    }

    [Fact]
    public void LabelSearchTerm_QuotesTheLabel_SoASpaceDoesNotSplitIntoAFreeTextWord()
    {
        // "label:in progress" unquoted would parse on GitHub's side as the qualifier "label:in" plus the free-text
        // word "progress" — matching almost every issue rather than the ones actually labelled "in progress".
        Assert.Equal("label:\"in progress\"", GitHubGhClient.LabelSearchTerm("in progress"));
    }

    [Theory]
    [InlineData("bug#42")]
    [InlineData("café")]
    [InlineData("a, b")]
    public void LabelSearchTerm_PassesUnicodeHashAndCommaThroughUnchanged(string label)
    {
        Assert.Equal($"label:\"{label}\"", GitHubGhClient.LabelSearchTerm(label));
    }

    [Fact]
    public void LabelListArguments_AsksOneRepositoryForNamesOnly_AtTheDocumentedLimit()
    {
        var arguments = GitHubGhClient.LabelListArguments("octocat/hello-world");

        Assert.True(SequenceAssert.ContainsInOrder(arguments, "label", "list"));
        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--repo", "octocat/hello-world"));
        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--json", "name"));
        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--limit", GitHubGhClient.LabelListLimit.ToString()));
    }
}
