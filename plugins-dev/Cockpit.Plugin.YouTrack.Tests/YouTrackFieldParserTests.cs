namespace Cockpit.Plugin.YouTrack.Tests;

// `YouTrackFieldParser` (#75): finding an issue's status field in a project that is free to call it
// whatever it likes ("State", "Stage", "Kanban State"), reading what it may become, and telling a
// workflow-governed field — where the allowed moves are events, not values — from an ordinary one.
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

        Assert.NotNull(fields.State);
        Assert.Equal("State", fields.State!.Name);
        Assert.Equal("StateIssueCustomField", fields.State.Type);
        Assert.Equal("Open", fields.State.CurrentValue);
        Assert.Equal(new[] { "Open", "In Progress", "Done" }, fields.State.Values);
        Assert.False(fields.State.IsStateMachine);
    }

    [Fact]
    public void AvailableTargets_OnAnOrdinaryField_LeavesOutTheStateTheIssueIsAlreadyIn()
    {
        var field = new YouTrackStateField("1", "State", "StateIssueCustomField", "In Progress", ["Open", "In Progress", "Done"], []);

        Assert.Equal(new[] { "Open", "Done" }, field.AvailableTargets);
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

        Assert.Equal("Stage", fields.State!.Name);
        Assert.Equal("Backlog", fields.State.CurrentValue);
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

        Assert.Equal("State", fields.State!.Name);
    }

    [Fact]
    public void Parse_WithNoStatusFieldAtAll_ReportsNone()
    {
        var fields = YouTrackFieldParser.Parse("""[{"id":"5","name":"Priority","$type":"SingleEnumIssueCustomField","value":{"name":"Normal"}}]""");

        Assert.Null(fields.State);
        Assert.Null(fields.AssigneeFieldName);
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

        Assert.Equal("Assignee", fields.AssigneeFieldName);
    }

    [Fact]
    public void ParsePossibleEvents_ReadsTheTransitionsAWorkflowAllowsFromHere()
    {
        var events = YouTrackFieldParser.ParsePossibleEvents(
            """
            {"$type":"StateMachineIssueCustomField","possibleEvents":[{"id":"e1","presentation":"start progress"},{"id":"e2","presentation":"reject"}]}
            """);

        Assert.Equal(new[] { "start progress", "reject" }, events.Select(possibleEvent => possibleEvent.Presentation));
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

        Assert.True(field.IsStateMachine);
        Assert.Equal(new[] { "start progress" }, field.AvailableTargets);
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

        Assert.Equal(new[] { "Open", "Review", "Done" }, values);
    }

    [Fact]
    public void ParseProjectStateField_FindsTheFieldByPreferenceAndReturnsItsName()
    {
        var (fieldName, values) = YouTrackFieldParser.ParseProjectStateField(
            """
            [
              {"field":{"name":"Priority"},"bundle":{"values":[{"name":"Low"}]}},
              {"field":{"name":"State"},"bundle":{"values":[{"name":"Open"},{"name":"Review"},{"name":"Done"}]}}
            ]
            """);

        Assert.Equal("State", fieldName);
        Assert.Equal(new[] { "Open", "Review", "Done" }, values);
    }

    [Fact]
    public void ParseProjectStateField_WhenTheProjectCallsItStage_FindsItAnyway()
    {
        var (fieldName, values) = YouTrackFieldParser.ParseProjectStateField(
            """[{"field":{"name":"Stage"},"bundle":{"values":[{"name":"Backlog"},{"name":"Ready"}]}}]""");

        Assert.Equal("Stage", fieldName);
        Assert.Equal(new[] { "Backlog", "Ready" }, values);
    }

    [Fact]
    public void ParseProjectStateField_WhenABoardHasBothStateAndKanbanState_PrefersState()
    {
        var (fieldName, _) = YouTrackFieldParser.ParseProjectStateField(
            """
            [
              {"field":{"name":"Kanban State"},"bundle":{"values":[{"name":"Ready"}]}},
              {"field":{"name":"State"},"bundle":{"values":[{"name":"Open"}]}}
            ]
            """);

        Assert.Equal("State", fieldName);
    }

    [Fact]
    public void ParseProjectStateField_ExcludesAValueWhoseIsResolvedIsTrue()
    {
        // AC-518 follow-up: the state filter always queries with #Unresolved, so a resolved value (Done) would be
        // an option that reads as present but returns nothing every time it is chosen.
        var (_, values) = YouTrackFieldParser.ParseProjectStateField(
            """
            [
              {"field":{"name":"State"},"bundle":{"values":[
                {"name":"Open","isResolved":false},
                {"name":"Done","isResolved":true}
              ]}}
            ]
            """);

        Assert.Equal(["Open"], values);
    }

    [Fact]
    public void ParseProjectStateField_KeepsAValueWhoseIsResolvedIsJsonNull()
    {
        // Undocumented what YouTrack sends when isResolved does not apply — treated as "cannot confirm resolved",
        // never as "treat as resolved": a value disappearing from the filter is worse than one that returns empty.
        var (_, values) = YouTrackFieldParser.ParseProjectStateField(
            """[{"field":{"name":"State"},"bundle":{"values":[{"name":"Done","isResolved":null}]}}]""");

        Assert.Equal(["Done"], values);
    }

    [Fact]
    public void ParseProjectStateField_KeepsAValueWithNoIsResolvedPropertyAtAll()
    {
        // The EnumBundle shape a Stage/Kanban State field runs on (as opposed to a StateBundle) — its elements
        // carry no isResolved key at all, and this must degrade to "keep everything", not drop silently.
        var (_, values) = YouTrackFieldParser.ParseProjectStateField(
            """[{"field":{"name":"Stage"},"bundle":{"values":[{"name":"Done"}]}}]""");

        Assert.Equal(["Done"], values);
    }

    [Fact]
    public void ParseProjectFieldValues_KeepsAResolvedValue_UnlikeParseProjectStateField()
    {
        // The per-issue Set-state menu (GetIssueFieldsAsync's fallback route) has to be able to offer moving an
        // issue TO Done — only the state filter's own dropdown excludes resolved values.
        var values = YouTrackFieldParser.ParseProjectFieldValues(
            """[{"field":{"name":"State"},"bundle":{"values":[{"name":"Open","isResolved":false},{"name":"Done","isResolved":true}]}}]""",
            "State");

        Assert.Equal(["Open", "Done"], values);
    }

    [Fact]
    public void ParseProjectStateField_WithNoRecognizedStatusField_ReportsNone()
    {
        var (fieldName, values) = YouTrackFieldParser.ParseProjectStateField(
            """[{"field":{"name":"Priority"},"bundle":{"values":[{"name":"Low"}]}}]""");

        Assert.Null(fieldName);
        Assert.Empty(values);
    }

    [Fact]
    public void ParseProjectStateField_WithAnEmptyProject_ReportsNone()
    {
        var (fieldName, values) = YouTrackFieldParser.ParseProjectStateField("[]");

        Assert.Null(fieldName);
        Assert.Empty(values);
    }
}
