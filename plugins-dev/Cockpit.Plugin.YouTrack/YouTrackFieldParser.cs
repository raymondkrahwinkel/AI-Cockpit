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

    /// <summary>
    /// The name of the state an issue stands on, from its <c>customFields</c> array — the same "which field is the
    /// status" rule <see cref="Parse"/> applies, over an already-parsed element. Null when the array holds no status
    /// field, or one with no value set. Autopilot's start gate reads this, so answering it by a different rule than
    /// the workflow actions use would let the two disagree about what stage an issue is on.
    /// </summary>
    public static string? ParseStateName(JsonElement customFields)
    {
        if (customFields.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var fields = customFields.EnumerateArray().ToList();
        foreach (var name in StateFieldNames)
        {
            var match = fields.FirstOrDefault(field => string.Equals(_Name(field), name, StringComparison.Ordinal));
            if (match.ValueKind == JsonValueKind.Object
                && match.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty("name", out var valueName) && valueName.ValueKind == JsonValueKind.String)
            {
                return valueName.GetString();
            }
        }

        return null;
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

    /// <summary>
    /// Whichever of a project's fields is its status — same State/Stage/Kanban State preference <see cref="Parse"/>
    /// and <see cref="ParseStateName"/> apply — from the admin projects/customFields response. Unlike
    /// <see cref="ParseProjectFieldValues"/>, which reads one already-known field by name and keeps every value
    /// (including a resolved one — the per-issue Set-state menu has to be able to move an issue <em>to</em> Done),
    /// this excludes resolved values: the dialog's state filter always queries with <c>#Unresolved</c> (AC-518
    /// follow-up), so offering "Done" would be a choice that reads as present but always returns nothing. The
    /// field's own name travels back with its values so a caller can query by it later.
    /// </summary>
    public static (string? FieldName, IReadOnlyList<string> Values) ParseProjectStateField(string projectCustomFieldsJson)
    {
        using var document = JsonDocument.Parse(projectCustomFieldsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return (null, []);
        }

        var fields = document.RootElement.EnumerateArray().ToList();
        foreach (var name in StateFieldNames)
        {
            var match = fields.FirstOrDefault(field =>
                field.TryGetProperty("field", out var fieldElement)
                && string.Equals(_String(fieldElement, "name"), name, StringComparison.Ordinal));

            if (match.ValueKind == JsonValueKind.Object)
            {
                return (name, _BundleValues(match, excludeResolved: true));
            }
        }

        return (null, []);
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

    // excludeResolved leaves out a value whose own isResolved is the literal JSON true (StateBundleElement only —
    // JetBrains devportal). Absent or JSON null — an EnumBundle-backed Stage/Kanban-State field carries neither,
    // and what YouTrack does when isResolved is requested on one is not documented (AC-518 follow-up) — keeps the
    // value: a value this cannot confirm is resolved must never disappear from the state filter, that would be
    // worse than showing one that turns out empty. ParseProjectFieldValues never passes excludeResolved: the
    // per-issue Set-state menu has to be able to offer moving an issue to a resolved value, not just away from one.
    private static IReadOnlyList<string> _BundleValues(JsonElement projectCustomField, bool excludeResolved = false)
    {
        if (!projectCustomField.TryGetProperty("bundle", out var bundle)
            || !bundle.TryGetProperty("values", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values
            .EnumerateArray()
            .Where(value => !excludeResolved || !_IsResolved(value))
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

    private static bool _IsResolved(JsonElement value) =>
        value.TryGetProperty("isResolved", out var isResolved) && isResolved.ValueKind == JsonValueKind.True;
}
