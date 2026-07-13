using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// The JSON bodies the issue-update endpoint (<c>POST {base}/issues/{id}</c>) takes. Built here, pure, because
/// the shape is unforgiving: the field's own <c>$type</c> has to be echoed back — a wrong one is answered with a
/// 500, not a validation error — and a state-machine field is moved by firing an <c>event</c>, not by writing a
/// value.
/// </summary>
internal static class YouTrackUpdateBody
{
    /// <summary>Moves an issue's status: fires the named event on a state-machine field, writes the value on an ordinary one.</summary>
    public static string ForState(YouTrackStateField field, string target)
    {
        var customField = new JsonObject
        {
            ["name"] = field.Name,
            ["$type"] = field.Type,
        };

        if (field.IsStateMachine)
        {
            customField["event"] = target;
        }
        else
        {
            customField["value"] = new JsonObject { ["name"] = target };
        }

        return _Wrap(customField);
    }

    /// <summary>Sets the Assignee field to one user, addressed by login.</summary>
    public static string ForAssignee(string fieldName, string login) =>
        _Wrap(new JsonObject
        {
            ["name"] = fieldName,
            ["$type"] = "SingleUserIssueCustomField",
            ["value"] = new JsonObject { ["login"] = login },
        });

    private static string _Wrap(JsonObject customField) =>
        new JsonObject { ["customFields"] = new JsonArray(customField) }.ToJsonString(new JsonSerializerOptions());
}
