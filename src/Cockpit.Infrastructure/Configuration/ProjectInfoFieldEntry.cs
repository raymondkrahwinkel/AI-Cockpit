using System.Text.Json.Serialization;
using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Configuration;

// AC-318: on-disk shape of a `ProjectInfoField`. Label is the operator's own words, never a lookup key.
// A credential goes under `SecretValue`, which `SecretFields` recognises and encrypts automatically —
// same route as `ProfileEnvironmentVariableEntry`; an ordinary value stays readable under `Value`.
internal sealed class ProjectInfoFieldEntry
{
    // Nullable because a hand-edited config can write `null` here, and the deserializer assigns it: the domain row takes strings, so the null is answered at this boundary rather than by every reader of it.
    public string? Label { get; set; }

    // The value when it is not a credential.
    public string? Value { get; set; }

    // The value when it is a credential; the field's name is what routes it through encryption.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SecretValue { get; set; }

    // Absent for a row no session is told about, which is the default and most of them.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsSharedWithSessions { get; set; }

    public static ProjectInfoFieldEntry FromDomain(ProjectInfoField field) => new()
    {
        Label = field.Label,
        Value = field.IsSecret ? null : field.Value,
        SecretValue = field.IsSecret ? field.Value : null,
        IsSharedWithSessions = field.IsSharedWithSessions,
    };

    // Which of the two fields carries the value is also what says whether it is a secret — one fact on disk rather
    // than a value plus a separate flag that a hand edit could leave disagreeing with it.
    public ProjectInfoField ToDomain() => new(Label ?? string.Empty, SecretValue ?? Value ?? string.Empty)
    {
        IsSecret = SecretValue is not null,
        IsSharedWithSessions = IsSharedWithSessions,
    };
}
