
namespace Cockpit.Plugin.YouTrack.Tests;

// AC-299 bug 2: a Start work or Set state call reloads the grid from a fresh fetch, and that new list is a
// different collection of different `YouTrackIssue` instances — including one whose `State` is
// now different from the one that was selected. `IssueSelection.Restore` is what
// `YouTrackDialogControl` uses to find the same issue again, by `YouTrackIssue.IdReadable`
// rather than by object or structural equality.
public class IssueSelectionTests
{
    private static readonly YouTrackIssue Backlog = new("1-1", "AT-1", "Faster startup", null, "AT", "Backlog");
    private static readonly YouTrackIssue Other = new("1-2", "AT-2", "Fix the sidebar", null, "AT", "Backlog");

    [Fact]
    public void FindsTheSameIssue_ById_EvenWhenItsFieldsChanged()
    {
        // A brand-new record with the same IdReadable, but a State that just moved — the exact shape a reload
        // hands back right after Start work or Set state.
        var moved = Backlog with { State = "In Progress" };

        var restored = IssueSelection.Restore([moved, Other], Backlog.IdReadable);

        Assert.Equal(moved, restored);
    }

    [Fact]
    public void ReturnsNull_WhenTheIssueIsNoLongerInTheList()
    {
        // Filtered out by the state dropdown, say — there is nothing to reselect, and it is not this method's job
        // to invent something.
        var restored = IssueSelection.Restore([Other], Backlog.IdReadable);

        Assert.Null(restored);
    }

    [Fact]
    public void ReturnsNull_ForABlankOrMissingId()
    {
        Assert.Null(IssueSelection.Restore([Backlog, Other], null));
        Assert.Null(IssueSelection.Restore([Backlog, Other], string.Empty));
    }
}
