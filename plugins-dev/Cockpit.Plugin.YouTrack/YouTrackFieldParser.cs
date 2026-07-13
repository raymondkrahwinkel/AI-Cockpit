using System.Text.Json;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// Reads an issue's <c>customFields</c> response into the two fields the workflow actions care about. Pure, so
/// the awkward part — which of a project's fields <em>is</em> the status, given that it is called "State" here,
/// "Stage" there and "Kanban State" on a third board — is decided by a rule that can be tested without a
/// YouTrack to talk to.
/// </summary>
internal static class YouTrackFieldParser
{
    // The names a status field goes by, most specific first: a board that has both "State" and "Kanban State"
    // means the former, which is why this is an ordered preference and not a set.
    private static readonly string[] StateFieldNames = ["State", "Stage", "Kanban State"];

    private const string AssigneeFieldName = "Assignee";

    public static YouTrackIssueFields Parse(string customFieldsJson)
    {
        using var document = JsonDocument.Parse(customFieldsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new YouTrackIssueFields(null, null);
        }

        var fields = document.RootElement.EnumerateArray().ToList();
        var assignee = fields
            .FirstOrDefault(field => string.Equals(_Name(field), AssigneeFieldName, StringComparison.Ordinal));

        foreach (var name in StateFieldNames)
        {
            var match = fields.FirstOrDefault(field => string.Equals(_Name(field), name, StringComparison.Ordinal));
            if (match.ValueKind == JsonValueKind.Object)
            {
                return new YouTrackIssueFields(_ToStateField(match, name), _NullIfAbsent(assignee, AssigneeFieldName));
            }
        }

        return new YouTrackIssueFields(null, _NullIfAbsent(assignee, AssigneeFieldName));
    }

    /// <summary>The transitions a workflow allows from where the issue stands now, from a state-machine field's own response.</summary>
    public static IReadOnlyList<YouTrackStateEvent> ParsePossibleEvents(string fieldJson)
    {
        using var document = JsonDocument.Parse(fieldJson);
        if (!document.RootElement.TryGetProperty("possibleEvents", out var events) || events.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return events
            .EnumerateArray()
            .Select(possibleEvent => new YouTrackStateEvent(
                _String(possibleEvent, "id") ?? string.Empty,
                _String(possibleEvent, "presentation") ?? string.Empty))
            .Where(possibleEvent => possibleEvent.Presentation.Length > 0)
            .ToList();
    }

    /// <summary>The values a project allows for one field, from the admin projects/customFields response — the route the plain issue read does not always carry.</summary>
    public static IReadOnlyList<string> ParseProjectFieldValues(string projectCustomFieldsJson, string fieldName)
    {
        using var document = JsonDocument.Parse(projectCustomFieldsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (var projectField in document.RootElement.EnumerateArray())
        {
            if (!projectField.TryGetProperty("field", out var field)
                || !string.Equals(_String(field, "name"), fieldName, StringComparison.Ordinal))
            {
                continue;
            }

            return _BundleValues(projectField);
        }

        return [];
    }

    private static YouTrackStateField _ToStateField(JsonElement field, string name) =>
        new(
            _String(field, "id") ?? string.Empty,
            name,
            _String(field, "$type") ?? string.Empty,
            field.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object
                ? _String(value, "name")
                : null,
            field.TryGetProperty("projectCustomField", out var projectCustomField) ? _BundleValues(projectCustomField) : [],
            []);

    private static IReadOnlyList<string> _BundleValues(JsonElement projectCustomField)
    {
        if (!projectCustomField.TryGetProperty("bundle", out var bundle)
            || !bundle.TryGetProperty("values", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values
            .EnumerateArray()
            .Select(value => _String(value, "name"))
            .Where(value => value is { Length: > 0 })
            .Select(value => value!)
            .ToList();
    }

    private static string? _Name(JsonElement field) => _String(field, "name");

    private static string? _NullIfAbsent(JsonElement field, string name) =>
        field.ValueKind == JsonValueKind.Object ? name : null;

    private static string? _String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
