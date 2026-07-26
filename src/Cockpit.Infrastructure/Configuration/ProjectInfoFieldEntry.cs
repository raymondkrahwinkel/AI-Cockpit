using System.Text.Json.Serialization;
using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of a <see cref="ProjectInfoField"/> inside a <see cref="ProjectEntry"/>. The label is the operator's
/// own words and is never a key the cockpit looks anything up by.
/// <para>
/// A credential is stored under <see cref="SecretValue"/> — a field name the secret rule recognises
/// (<c>SecretFields</c>), so it is encrypted at rest and scrubbed from backups without this entry knowing how
/// (AC-318, the same route <c>ProfileEnvironmentVariableEntry</c> takes). An ordinary value stays readable in
/// <c>cockpit.json</c> under <see cref="Value"/>, on purpose: the config file is the operator's to inspect.
/// </para>
/// </summary>
internal sealed class ProjectInfoFieldEntry
{
    /// <summary>Nullable because a hand-edited config can write <c>null</c> here, and the deserializer assigns it: the domain row takes strings, so the null is answered at this boundary rather than by every reader of it.</summary>
    public string? Label { get; set; }

    /// <summary>The value when it is not a credential.</summary>
    public string? Value { get; set; }

    /// <summary>The value when it is a credential; the field's name is what routes it through encryption.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SecretValue { get; set; }

    /// <summary>Absent for a row no session is told about, which is the default and most of them.</summary>
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
