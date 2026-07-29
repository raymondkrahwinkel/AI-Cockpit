using System.Text.Json;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// <see cref="YouTrackUpdateBody"/> (#75): the update body YouTrack accepts. Asserted because the shape is
/// unforgiving — the field's own <c>$type</c> must come back verbatim (a wrong one answers 500, not a
/// validation error), and a workflow-governed field is moved by firing an event, never by writing a value.
/// </summary>
public class YouTrackUpdateBodyTests
{
    [Fact]
    public void ForState_OnAnOrdinaryField_WritesTheValueWithTheFieldsOwnType()
    {
        var field = new YouTrackStateField("1", "Stage", "StateIssueCustomField", "Open", ["Open", "Done"], []);

        var customField = _SingleCustomField(YouTrackUpdateBody.ForState(field, "Done"));

        Assert.Equal("Stage", customField.GetProperty("name").GetString());
        Assert.Equal("StateIssueCustomField", customField.GetProperty("$type").GetString());
        Assert.Equal("Done", customField.GetProperty("value").GetProperty("name").GetString());
        Assert.False(customField.TryGetProperty("event", out _));
    }

    [Fact]
    public void ForState_OnAStateMachineField_FiresTheEventInsteadOfWritingAValue()
    {
        var field = new YouTrackStateField(
            "2",
            "State",
            YouTrackStateField.StateMachineType,
            "Submitted",
            [],
            [new YouTrackStateEvent("e1", "start progress")]);

        var customField = _SingleCustomField(YouTrackUpdateBody.ForState(field, "start progress"));

        Assert.Equal(YouTrackStateField.StateMachineType, customField.GetProperty("$type").GetString());
        Assert.Equal("start progress", customField.GetProperty("event").GetString());
        Assert.False(customField.TryGetProperty("value", out _));
    }

    [Fact]
    public void ForAssignee_AddressesTheUserByLogin()
    {
        var customField = _SingleCustomField(YouTrackUpdateBody.ForAssignee("Assignee", "raymond"));

        Assert.Equal("Assignee", customField.GetProperty("name").GetString());
        Assert.Equal("SingleUserIssueCustomField", customField.GetProperty("$type").GetString());
        Assert.Equal("raymond", customField.GetProperty("value").GetProperty("login").GetString());
    }

    private static JsonElement _SingleCustomField(string body)
    {
        using var document = JsonDocument.Parse(body);
        var fields = document.RootElement.GetProperty("customFields");
        Assert.Equal(1, fields.GetArrayLength());

        return fields[0].Clone();
    }
}
