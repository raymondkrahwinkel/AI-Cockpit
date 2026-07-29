
namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// <see cref="YouTrackWorkflow.FindStartTarget"/> (#75): "start this ticket" means something different on every
/// board — a value called "In Progress" here, an event called "start progress" there — and on a board that has
/// neither it means nothing, in which case Start must not be offered at all.
/// </summary>
public class YouTrackWorkflowTests
{
    [Fact]
    public void FindStartTarget_PrefersTheStateActuallyCalledInProgress()
    {
        var field = new YouTrackStateField("1", "State", "StateIssueCustomField", "Open", ["Open", "In Progress", "In Review"], []);

        Assert.Equal("In Progress", YouTrackWorkflow.FindStartTarget(field));
    }

    [Fact]
    public void FindStartTarget_OnAStateMachineBoard_PicksTheEventThatStartsProgress()
    {
        var field = new YouTrackStateField(
            "2",
            "State",
            YouTrackStateField.StateMachineType,
            "Submitted",
            [],
            [new YouTrackStateEvent("e1", "reject"), new YouTrackStateEvent("e2", "start progress")]);

        Assert.Equal("start progress", YouTrackWorkflow.FindStartTarget(field));
    }

    [Fact]
    public void FindStartTarget_WhenTheIssueIsAlreadyInProgress_OffersNothing()
    {
        var field = new YouTrackStateField("3", "State", "StateIssueCustomField", "In Progress", ["Open", "In Progress"], []);

        Assert.Null(YouTrackWorkflow.FindStartTarget(field));
    }

    [Fact]
    public void FindStartTarget_OnABoardWithoutAnInProgressStep_OffersNothingRatherThanGuessing()
    {
        var field = new YouTrackStateField("4", "Stage", "StateIssueCustomField", "Backlog", ["Backlog", "Done"], []);

        Assert.Null(YouTrackWorkflow.FindStartTarget(field));
    }
}
