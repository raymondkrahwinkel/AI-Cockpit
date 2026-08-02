
namespace Cockpit.Plugin.GitHubIssues.Tests;

// A Refresh or "Assigned to me" toggle reloads the grid from a fresh fetch, and that new list is a different
// collection of different `GitHubIssue` instances. `IssueSelection.Restore` is what
// `GitHubIssuesDialogControl` uses to find the same issue again, by repository + number rather than by
// object or structural equality — the same defect and fix as the YouTrack dialog's `IssueSelection`.
public class IssueSelectionTests
{
    private static readonly GitHubIssue Selected = new(42, "Fix the login redirect", "https://github.com/octocat/hello-world/issues/42", null, "octocat/hello-world");
    private static readonly GitHubIssue Other = new(43, "Update the README", "https://github.com/octocat/hello-world/issues/43", null, "octocat/hello-world");

    // Same number, different repository — the CLI mode lists issues across every repo an owner has, so number
    // alone must never be treated as the identity.
    private static readonly GitHubIssue SameNumberDifferentRepo = new(42, "A different issue entirely", "https://github.com/octocat/other-repo/issues/42", null, "octocat/other-repo");

    [Fact]
    public void FindsTheSameIssue_ById_EvenWhenItsFieldsChanged()
    {
        var reloaded = Selected with { Title = "Fix the login redirect (edited)" };

        var restored = IssueSelection.Restore([reloaded, Other, SameNumberDifferentRepo], Selected.Repository, Selected.Number);

        Assert.Equal(reloaded, restored);
    }

    [Fact]
    public void DoesNotMatchTheSameNumber_InADifferentRepository()
    {
        var restored = IssueSelection.Restore([SameNumberDifferentRepo], Selected.Repository, Selected.Number);

        Assert.Null(restored);
    }

    [Fact]
    public void ReturnsNull_WhenTheIssueIsNoLongerInTheList()
    {
        var restored = IssueSelection.Restore([Other], Selected.Repository, Selected.Number);

        Assert.Null(restored);
    }

    [Fact]
    public void ReturnsNull_ForAMissingRepositoryOrNumber()
    {
        Assert.Null(IssueSelection.Restore([Selected, Other], null, Selected.Number));
        Assert.Null(IssueSelection.Restore([Selected, Other], Selected.Repository, null));
    }
}
