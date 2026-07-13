using FluentAssertions;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// <see cref="YouTrackFieldParser"/> (#75): finding an issue's status field in a project that is free to call it
/// whatever it likes ("State", "Stage", "Kanban State"), reading what it may become, and telling a
/// workflow-governed field — where the allowed moves are events, not values — from an ordinary one.
/// </summary>
public class YouTrackFieldParserTests
{
    [Fact]
    public void Parse_ReadsTheStateFieldWithItsCurrentValueAndTheProjectsValues()
    {
        var fields = YouTrackFieldParser.Parse(
            """
            [
              {"id":"1","name":"State","$type":"StateIssueCustomField","value":{"name":"Open"},
               "projectCustomField":{"field":{"name":"State"},"bundle":{"values":[{"name":"Open"},{"name":"In Progress"},{"name":"Done"}]}}}
            ]
            """);

        fields.State.Should().NotBeNull();
        fields.State!.Name.Should().Be("State");
        fields.State.Type.Should().Be("StateIssueCustomField");
        fields.State.CurrentValue.Should().Be("Open");
        fields.State.Values.Should().Equal("Open", "In Progress", "Done");
        fields.State.IsStateMachine.Should().BeFalse();
    }

    [Fact]
    public void AvailableTargets_OnAnOrdinaryField_LeavesOutTheStateTheIssueIsAlreadyIn()
    {
        var field = new YouTrackStateField("1", "State", "StateIssueCustomField", "In Progress", ["Open", "In Progress", "Done"], []);

        field.AvailableTargets.Should().Equal("Open", "Done");
    }

    [Fact]
    public void Parse_WhenTheProjectCallsItStage_FindsItAnyway()
    {
        var fields = YouTrackFieldParser.Parse(
            """
            [
              {"id":"2","name":"Stage","$type":"StateIssueCustomField","value":{"name":"Backlog"}}
            ]
            """);

        fields.State!.Name.Should().Be("Stage");
        fields.State.CurrentValue.Should().Be("Backlog");
    }

    [Fact]
    public void Parse_WhenABoardHasBothStateAndKanbanState_PrefersState()
    {
        var fields = YouTrackFieldParser.Parse(
            """
            [
              {"id":"3","name":"Kanban State","$type":"StateIssueCustomField","value":{"name":"Ready"}},
              {"id":"4","name":"State","$type":"StateIssueCustomField","value":{"name":"Open"}}
            ]
            """);

        fields.State!.Name.Should().Be("State");
    }

    [Fact]
    public void Parse_WithNoStatusFieldAtAll_ReportsNone()
    {
        var fields = YouTrackFieldParser.Parse("""[{"id":"5","name":"Priority","$type":"SingleEnumIssueCustomField","value":{"name":"Normal"}}]""");

        fields.State.Should().BeNull();
        fields.AssigneeFieldName.Should().BeNull();
    }

    [Fact]
    public void Parse_FindsTheAssigneeFieldWhenTheProjectHasOne()
    {
        var fields = YouTrackFieldParser.Parse(
            """
            [
              {"id":"6","name":"Assignee","$type":"SingleUserIssueCustomField","value":{"name":"raymond"}},
              {"id":"7","name":"State","$type":"StateIssueCustomField","value":{"name":"Open"}}
            ]
            """);

        fields.AssigneeFieldName.Should().Be("Assignee");
    }

    [Fact]
    public void ParsePossibleEvents_ReadsTheTransitionsAWorkflowAllowsFromHere()
    {
        var events = YouTrackFieldParser.ParsePossibleEvents(
            """
            {"$type":"StateMachineIssueCustomField","possibleEvents":[{"id":"e1","presentation":"start progress"},{"id":"e2","presentation":"reject"}]}
            """);

        events.Select(possibleEvent => possibleEvent.Presentation).Should().Equal("start progress", "reject");
    }

    [Fact]
    public void AvailableTargets_OnAStateMachineField_AreTheEventsNotTheValues()
    {
        var field = new YouTrackStateField(
            "8",
            "State",
            YouTrackStateField.StateMachineType,
            "Submitted",
            ["Submitted", "In Progress", "Done"],
            [new YouTrackStateEvent("e1", "start progress")]);

        field.IsStateMachine.Should().BeTrue();
        field.AvailableTargets.Should().Equal("start progress");
    }

    [Fact]
    public void ParseProjectFieldValues_ReadsTheBundleOfTheNamedField()
    {
        var values = YouTrackFieldParser.ParseProjectFieldValues(
            """
            [
              {"field":{"name":"Priority"},"bundle":{"values":[{"name":"Low"}]}},
              {"field":{"name":"State"},"bundle":{"values":[{"name":"Open"},{"name":"Review"},{"name":"Done"}]}}
            ]
            """,
            "State");

        values.Should().Equal("Open", "Review", "Done");
    }
}
