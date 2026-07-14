using FluentAssertions;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// The "Ticket status changed" trigger (#69). A flow that runs when a ticket reaches Review has to be told which
/// status it reached, and which one it left — "the ticket moved" is not something a flow can decide on.
/// </summary>
public class IssueStateChangesTests
{
    [Fact]
    public void AMove_SaysWhichTicketWentWhere_AndWhereItCameFrom()
    {
        var changes = new IssueStateChanges();
        IssueStateChanged? heard = null;
        changes.Changed += (_, moved) => heard = moved;

        changes.Moved(Instance, Issue, previousState: "In Progress", newState: "Review", workingDirectory: "/repo");

        heard.Should().NotBeNull();
        heard!.Issue.IdReadable.Should().Be("EVE-14");
        heard.PreviousState.Should().Be("In Progress");
        heard.NewState.Should().Be("Review");
        heard.WorkingDirectory.Should().Be("/repo");
    }

    // Nothing is listening until a flow starts with this trigger, and a move made then must not fall over.
    [Fact]
    public void AMoveWithNoFlowListening_DoesNothing()
    {
        var changes = new IssueStateChanges();

        var move = () => changes.Moved(Instance, Issue, "In Progress", "Review");

        move.Should().NotThrow();
    }

    private static YouTrackInstance Instance => new("Work", "https://youtrack.example.com", "token", "EVE");

    private static YouTrackIssue Issue => new("1", "EVE-14", "Fix the login redirect", null, "EVE", "In Progress");
}
