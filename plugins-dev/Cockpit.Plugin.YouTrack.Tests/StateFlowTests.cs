using FluentAssertions;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// Which way a ticket may move (#75). Read from the board, never invented — and the difference between the two kinds
/// of board is the whole of it.
/// <para>
/// A state-machine project has a real transition graph and YouTrack hands it over; there is no "next column" to reason
/// about, only the events it allows. An ordinary status field has no graph at all — YouTrack will happily put a ticket
/// from Backlog straight to Released — so the only order there is is the order of the columns, which is what forward
/// and back mean here. Inventing a rule and blaming YouTrack for it is the thing these tests exist to prevent.
/// </para>
/// </summary>
public class StateFlowTests
{
    // Raymond's own board, read from the API: Stage, an ordinary field.
    private static readonly string[] Board = ["Backlog", "In Progress", "Review (GIT)", "Development", "Staging", "Released"];

    [Fact]
    public void ForwardIsTheNextColumn_AndBackIsThePreviousOne()
    {
        var state = _Ordinary("In Progress");

        StateFlow.Forward(state).Should().Be("Review (GIT)");
        StateFlow.Back(state).Should().Be("Backlog");
    }

    [Fact]
    public void AtTheStartOfTheBoard_ThereIsNowhereBack()
    {
        var state = _Ordinary("Backlog");

        StateFlow.Forward(state).Should().Be("In Progress");
        StateFlow.Back(state).Should().BeNull();
    }

    [Fact]
    public void AtTheEndOfTheBoard_ThereIsNowhereForward()
    {
        var state = _Ordinary("Released");

        StateFlow.Forward(state).Should().BeNull();
        StateFlow.Back(state).Should().Be("Staging");
    }

    [Fact]
    public void EverythingElse_IsStillOffered_BecauseYouTrackStillAllowsIt()
    {
        // A menu that hid the jumps would be lying about what the operator can do. They are one level down, not gone.
        var state = _Ordinary("In Progress");

        StateFlow.Elsewhere(state).Should().BeEquivalentTo(["Development", "Staging", "Released"]);
    }

    [Fact]
    public void AStateMachineBoard_HasNoForwardOrBack_BecauseItsEventsAreTheWholeTruth()
    {
        // Its transitions are a graph, not a line: asking for "the next column" would be asking a question the board
        // does not have an answer to.
        var state = new YouTrackStateField(
            "1",
            "State",
            YouTrackStateField.StateMachineType,
            "In Progress",
            [],
            [new YouTrackStateEvent("e1", "Fixed"), new YouTrackStateEvent("e2", "Reopen")]);

        StateFlow.Forward(state).Should().BeNull();
        StateFlow.Back(state).Should().BeNull();
        StateFlow.Elsewhere(state).Should().BeEquivalentTo(["Fixed", "Reopen"], "its events are the moves it allows");
    }

    [Fact]
    public void AStatusTheBoardDoesNotKnow_LeadsNowhere_RatherThanToAGuess()
    {
        var state = _Ordinary("Somewhere else entirely");

        StateFlow.Forward(state).Should().BeNull();
        StateFlow.Back(state).Should().BeNull();
    }

    private static YouTrackStateField _Ordinary(string current) =>
        new("1", "Stage", "StateIssueCustomField", current, Board, []);
}
