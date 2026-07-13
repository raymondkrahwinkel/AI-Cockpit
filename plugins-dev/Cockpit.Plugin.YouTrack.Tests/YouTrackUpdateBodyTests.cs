using System.Text.Json;
using FluentAssertions;

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

        customField.GetProperty("name").GetString().Should().Be("Stage");
        customField.GetProperty("$type").GetString().Should().Be("StateIssueCustomField");
        customField.GetProperty("value").GetProperty("name").GetString().Should().Be("Done");
        customField.TryGetProperty("event", out _).Should().BeFalse();
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

        customField.GetProperty("$type").GetString().Should().Be(YouTrackStateField.StateMachineType);
        customField.GetProperty("event").GetString().Should().Be("start progress");
        customField.TryGetProperty("value", out _).Should().BeFalse();
    }

    [Fact]
    public void ForAssignee_AddressesTheUserByLogin()
    {
        var customField = _SingleCustomField(YouTrackUpdateBody.ForAssignee("Assignee", "raymond"));

        customField.GetProperty("name").GetString().Should().Be("Assignee");
        customField.GetProperty("$type").GetString().Should().Be("SingleUserIssueCustomField");
        customField.GetProperty("value").GetProperty("login").GetString().Should().Be("raymond");
    }

    private static JsonElement _SingleCustomField(string body)
    {
        using var document = JsonDocument.Parse(body);
        var fields = document.RootElement.GetProperty("customFields");
        fields.GetArrayLength().Should().Be(1);

        return fields[0].Clone();
    }
}
