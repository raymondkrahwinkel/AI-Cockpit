using Cockpit.TestSupport;

namespace Cockpit.Plugin.GitHubIssues.Tests;

// The `gh` arguments this plugin builds (AC-519), asserted the same way `GitHubPrGhClientTests` asserts
// the pull-requests plugin's: without shelling out to a real `gh`. The real process path — a fake `gh` on
// PATH driving the actual `GitHubGhClient`, including failure and multi-repo scenarios — was measured
// separately in a disposable scratchpad harness; committed here is the query construction this repo keeps testing
// without a live process.
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
    public void LabelSearchTerm_QuotesTheLabel_SoASpaceDoesNotSplitIntoAFreeTextWord()
    {
        // "label:in progress" unquoted would parse on GitHub's side as the qualifier "label:in" plus the free-text
        // word "progress" — matching almost every issue rather than the ones actually labelled "in progress".
        Assert.Equal("label:\"in progress\"", GitHubGhClient.LabelSearchTerm("in progress"));
    }

    [Fact]
    public void SearchArguments_WithSeveralRepositories_AddsARepoFlagPerRepository_NotARepoSearchTerm()
    {
        // AC-940: `gh search issues` ANDs multiple `repo:` search terms — the fix is a `--repo` flag per repository
        // instead, which gh itself ORs. A `repo:` term anywhere here would be the exact bug this guards against.
        var arguments = GitHubGhClient.SearchArguments("octocat", assignedToMe: false, extraTerms: null, repositories: ["octocat/a", "octocat/b"]);

        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--repo", "octocat/a"));
        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--repo", "octocat/b"));
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("repo:", StringComparison.Ordinal));
    }
}
