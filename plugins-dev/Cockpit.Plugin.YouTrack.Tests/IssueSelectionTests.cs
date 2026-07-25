using FluentAssertions;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// AC-299 bug 2: a Start work or Set state call reloads the grid from a fresh fetch, and that new list is a
/// different collection of different <see cref="YouTrackIssue"/> instances — including one whose <c>State</c> is
/// now different from the one that was selected. <see cref="IssueSelection.Restore"/> is what
/// <c>YouTrackDialogControl</c> uses to find the same issue again, by <see cref="YouTrackIssue.IdReadable"/>
/// rather than by object or structural equality.
/// </summary>
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

        restored.Should().Be(moved);
    }

    [Fact]
    public void ReturnsNull_WhenTheIssueIsNoLongerInTheList()
    {
        // Filtered out by the state dropdown, say — there is nothing to reselect, and it is not this method's job
        // to invent something.
        var restored = IssueSelection.Restore([Other], Backlog.IdReadable);

        restored.Should().BeNull();
    }

    [Fact]
    public void ReturnsNull_ForABlankOrMissingId()
    {
        IssueSelection.Restore([Backlog, Other], null).Should().BeNull();
        IssueSelection.Restore([Backlog, Other], string.Empty).Should().BeNull();
    }
}
