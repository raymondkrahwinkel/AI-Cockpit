using System.Text.Json;

namespace Cockpit.Plugin.YouTrack.Tests;

// Which custom field is an issue's status, read off an already-parsed `customFields` array — the rule the issue
// grid and Autopilot's start gate (AC-345) now share. Asserted with xunit's own Assert rather than the
// FluentAssertions the older files in this project use: that package is commercially licensed from v8 on.
public class YouTrackStateNameTests
{
    [Theory]
    [InlineData("State")]
    [InlineData("Stage")]
    [InlineData("Kanban State")]
    public void ParseStateName_ReadsWhicheverNameTheProjectGivesItsStatusField(string fieldName)
    {
        var fields = _Parse($$"""[{ "name": "{{fieldName}}", "value": { "name": "Ready" } }]""");

        Assert.Equal("Ready", YouTrackFieldParser.ParseStateName(fields));
    }

    private static JsonElement _Parse(string json) => JsonDocument.Parse(json).RootElement;
}
