using System.Text.Json.Serialization;
using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of a <see cref="ProjectInfoField"/> inside a <see cref="ProjectEntry"/>. Both halves are the
/// operator's own words, so both are written as they were typed — nothing here is a key the cockpit looks anything
/// up by.
/// </summary>
internal sealed class ProjectInfoFieldEntry
{
    /// <summary>Nullable because a hand-edited config can write <c>null</c> here, and the deserializer assigns it: the domain row takes strings, so the null is answered at this boundary rather than by every reader of it.</summary>
    public string? Label { get; set; }

    public string? Value { get; set; }

    /// <summary>Absent for a row no session is told about, which is the default and most of them.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsSharedWithSessions { get; set; }

    public static ProjectInfoFieldEntry FromDomain(ProjectInfoField field) => new()
    {
        Label = field.Label,
        Value = field.Value,
        IsSharedWithSessions = field.IsSharedWithSessions,
    };

    public ProjectInfoField ToDomain() => new(Label ?? string.Empty, Value ?? string.Empty)
    {
        IsSharedWithSessions = IsSharedWithSessions,
    };
}
