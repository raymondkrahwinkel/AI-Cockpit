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
    public void SearchArguments_WithOneRepository_AddsARepoFlag()
    {
        var arguments = GitHubGhClient.SearchArguments("octocat", assignedToMe: false, extraTerms: null, repositories: ["octocat/hello-world"]);

        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--repo", "octocat/hello-world"));
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

    [Fact]
    public void SearchArguments_WithoutRepositories_CarriesNoRepoFlag()
    {
        var arguments = GitHubGhClient.SearchArguments("octocat", assignedToMe: false, extraTerms: null);

        Assert.DoesNotContain("--repo", arguments);
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

    [Fact]
    public void ApplyArchivedFilter_ExactlyAtTheSearchLimitWithArchivedIssuesMixedIn_StillReportsTruncation()
    {
        // AC-519 fix (adversarial review): gh's own page can come back at exactly the search limit and still filter
        // down to far fewer issues once archived-repo ones are excluded — this is the gh-path equivalent of the
        // HTTP path's pull-request fixture. WasTruncated has to be measured on the raw parsed page (the "issues"
        // argument here), not on what is left after this exclusion runs, or a repo full of archived-repo noise at
        // exactly the page size would silently stop warning.
        var issues = Enumerable.Range(1, 60)
            .Select(number => new GitHubIssue(number, $"Issue {number}", $"https://x/{number}", null, "octocat/kept"))
            .Concat(Enumerable.Range(1, 40).Select(number => new GitHubIssue(1000 + number, $"Archived {number}", $"https://x/{1000 + number}", null, "octocat/archived")))
            .ToList();
        var archived = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "octocat/archived" };
        Assert.Equal(GitHubGhClient.IssueSearchLimit, issues.Count);

        var (result, wasTruncated) = GitHubGhClient.ApplyArchivedFilter(issues, archived);

        Assert.Equal(60, result.Count);
        Assert.True(wasTruncated);
    }

    [Fact]
    public void ApplyArchivedFilter_NoArchivedRepositories_LeavesTheListUntouched()
    {
        var issues = Enumerable.Range(1, GitHubGhClient.IssueSearchLimit)
            .Select(number => new GitHubIssue(number, $"Issue {number}", $"https://x/{number}", null, "octocat/hello-world"))
            .ToList();

        var (result, wasTruncated) = GitHubGhClient.ApplyArchivedFilter(issues, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Same(issues, result);
        Assert.True(wasTruncated);
    }

    [Fact]
    public void ApplyArchivedFilter_OneShortOfTheSearchLimit_ReportsNotTruncatedEvenAfterFiltering()
    {
        var issues = Enumerable.Range(1, GitHubGhClient.IssueSearchLimit - 1)
            .Select(number => new GitHubIssue(number, $"Issue {number}", $"https://x/{number}", null, "octocat/archived"))
            .ToList();
        var archived = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "octocat/archived" };

        var (_, wasTruncated) = GitHubGhClient.ApplyArchivedFilter(issues, archived);

        Assert.False(wasTruncated);
    }

    [Fact]
    public void ApplyArchivedFilter_RepositoryNameComparisonStaysCaseInsensitive()
    {
        // The archived set is built with StringComparer.OrdinalIgnoreCase (_GetArchivedReposAsync); this proves the
        // AC-519 refactor kept that — a differently-cased repository name must still be excluded.
        var issues = new List<GitHubIssue> { new(1, "Issue", "https://x/1", null, "Octocat/Hello-World") };
        var archived = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "octocat/hello-world" };

        var (result, _) = GitHubGhClient.ApplyArchivedFilter(issues, archived);

        Assert.Empty(result);
    }
}
