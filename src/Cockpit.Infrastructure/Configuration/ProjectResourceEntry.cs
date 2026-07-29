using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of a <see cref="ProjectResource"/> inside a <see cref="ProjectEntry"/> (AC-483). Plain fields
/// rather than anything clever: unlike <c>PluginFields</c>, nothing here is a plugin's own key the host must carry
/// through blind, so there is no distinction to preserve between "recognised" and "not" the way that entry has.
/// </summary>
internal sealed class ProjectResourceEntry
{
    /// <summary>Nullable because a hand-edited config can write <c>null</c> here, and the deserializer assigns it: the domain row takes a string, so the null is answered at this boundary (<see cref="ToDomain"/>) rather than by every reader of it.</summary>
    public string? Reference { get; set; }

    /// <summary>
    /// The role as it reads on disk, kept as a plain string rather than the domain <see cref="ProjectResourceRole"/>
    /// itself (what this held before). Two failure modes made that the wrong choice: the file's shared
    /// <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/> throws for the <em>whole document</em>
    /// on a value it does not recognise (a typo, or a role a newer build added), which turns one bad row into
    /// every section of <c>cockpit.json</c> being declared damaged and rolled back to <c>.bak</c>; and a
    /// non-nullable enum reads a row that never wrote <c>role</c> at all as ordinal 0 —
    /// <see cref="ProjectResourceRole.Memory"/>, the one role this model itself documents as read <em>and written
    /// back to</em>. Handing the most powerful role to the row that forgot to ask for one is exactly backwards.
    /// <see cref="ToDomain"/> answers both cases the same way: a missing or unrecognised string becomes
    /// <see cref="ProjectResourceRole.Reference"/>, the least powerful role there is, so a bad or absent value
    /// costs one row's behavior rather than the whole file.
    /// </summary>
    public string? Role { get; set; }

    public string? Label { get; set; }

    public bool ReachesSessions { get; set; } = true;

    public static ProjectResourceEntry FromDomain(ProjectResource resource) => new()
    {
        Reference = resource.Reference,
        Role = resource.Role.ToString(),
        Label = resource.Label,
        ReachesSessions = resource.ReachesSessions,
    };

    public ProjectResource ToDomain() => new(Reference ?? string.Empty, _ParseRole(Role))
    {
        Label = Label,
        ReachesSessions = ReachesSessions,
    };

    /// <summary>
    /// <paramref name="role"/> parsed as a <see cref="ProjectResourceRole"/>, or <see cref="ProjectResourceRole.Reference"/>
    /// for anything that is not one of its member names — missing, blank, mis-typed, or a role only a newer build
    /// knows. The safe fallback rather than a thrown exception, so one stray row never costs the rest of the file
    /// (see the doc comment on <see cref="Role"/>).
    /// </summary>
    private static ProjectResourceRole _ParseRole(string? role) =>
        Enum.TryParse<ProjectResourceRole>(role, ignoreCase: true, out var parsed) ? parsed : ProjectResourceRole.Reference;
}
