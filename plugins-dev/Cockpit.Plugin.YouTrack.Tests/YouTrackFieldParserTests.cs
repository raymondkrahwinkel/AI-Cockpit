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
}
