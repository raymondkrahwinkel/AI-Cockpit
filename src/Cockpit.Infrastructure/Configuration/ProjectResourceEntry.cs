using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of a `ProjectResource` inside a `ProjectEntry` (AC-483). Plain fields
// rather than anything clever: unlike `PluginFields`, nothing here is a plugin's own key the host must carry
// through blind, so there is no distinction to preserve between "recognised" and "not" the way that entry has.
internal sealed class ProjectResourceEntry
{
    // Nullable because a hand-edited config can write `null` here, and the deserializer assigns it: the domain row takes a string, so the null is answered at this boundary (`ToDomain`) rather than by every reader of it.
    public string? Reference { get; set; }

    // The role as it reads on disk, kept as a plain string rather than the domain `ProjectResourceRole`
    // itself (what this held before). Two failure modes made that the wrong choice: the file's shared
    // `System.Text.Json.Serialization.JsonStringEnumConverter` throws for the *whole document*
    // on a value it does not recognise (a typo, or a role a newer build added), which turns one bad row into
    // every section of `cockpit.json` being declared damaged and rolled back to `.bak`; and a
    // non-nullable enum reads a row that never wrote `role` at all as ordinal 0 —
    // `ProjectResourceRole.Memory`, the one role this model itself documents as read *and written
    // back to*. Handing the most powerful role to the row that forgot to ask for one is exactly backwards.
    // `ToDomain` answers both cases the same way: a missing or unrecognised string becomes
    // `ProjectResourceRole.Reference`, the least powerful role there is, so a bad or absent value
    // costs one row's behavior rather than the whole file.
    public string? Role { get; set; }

    public string? Label { get; set; }

    public bool ReachesSessions { get; set; } = true;

    // Whether this row's contents travel with the session, not only its location (AC-486). Absent in a file written before that existed, which reads as false — the safe default, since it is the operator ticking a box that opens a file at all.
    public bool SendsContent { get; set; }

    public static ProjectResourceEntry FromDomain(ProjectResource resource) => new()
    {
        Reference = resource.Reference,
        Role = resource.Role.ToString(),
        Label = resource.Label,
        ReachesSessions = resource.ReachesSessions,
        SendsContent = resource.SendsContent,
    };

    public ProjectResource ToDomain()
    {
        var role = _ParseRole(Role);

        return new ProjectResource(Reference ?? string.Empty, role)
        {
            Label = Label,
            ReachesSessions = ReachesSessions,

            // Dropped outright for any role but Instructions, rather than carried and merely ignored (AC-486
            // review). The domain property already refuses to report it for another role, but that only masks it
            // while the role stays wrong: this file can be edited by hand, and a tick stored on a Memory row came
            // back the moment the operator changed that row to Instructions — arriving pre-ticked in front of
            // someone who never set it, and opening the file from the next session on. A flag that was never
            // offered must not survive the load at all, or every later `with { Role = … }` is a way to resurrect it.
            SendsContent = SendsContent && role == ProjectResourceRole.Instructions,
        };
    }

    // `role` parsed as a `ProjectResourceRole`, or `ProjectResourceRole.Reference`
    // for anything that is not one of its member names — missing, blank, mis-typed, or a role only a newer build
    // knows. The safe fallback rather than a thrown exception, so one stray row never costs the rest of the file
    // (see the doc comment on `Role`).
    private static ProjectResourceRole _ParseRole(string? role) =>
        Enum.TryParse<ProjectResourceRole>(role, ignoreCase: true, out var parsed) ? parsed : ProjectResourceRole.Reference;
}
