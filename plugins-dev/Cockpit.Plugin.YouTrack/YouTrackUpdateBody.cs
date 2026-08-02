using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cockpit.Plugin.YouTrack;

// The JSON bodies the issue-update endpoint (`POST {base}/issues/{id}`) takes. Built here, pure, because
// the shape is unforgiving: the field's own `$type` has to be echoed back — a wrong one is answered with a
// 500, not a validation error — and a state-machine field is moved by firing an `event`, not by writing a
// value.
internal static class YouTrackUpdateBody
{
    // Moves an issue's status: fires the named event on a state-machine field, writes the value on an ordinary one.
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

    // Sets the Assignee field to one user, addressed by login.
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
