using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of a `ProjectResource` inside a `ProjectEntry` (AC-483). Plain fields
// rather than anything clever: unlike `PluginFields`, nothing here is a plugin's own key the host must carry
// through blind, so there is no distinction to preserve between "recognised" and "not" the way that entry has.
internal sealed class ProjectResourceEntry
{
    // Nullable because a hand-edited config can write `null` here, and the deserializer assigns it: the domain row takes a string, so the null is answered at this boundary (`ToDomain`) rather than by every reader of it.
    public string? Reference { get; set; }

    // Role kept as a plain string: the shared enum converter throws the whole document on one bad value,
    // and a non-nullable enum would default a missing role to `Memory`. `ToDomain` maps both to `Reference`.
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

            // AC-486: dropped outright for any role but Instructions, not merely carried unread — a hand
            // edit can change a row's role, and a stale tick would resurface pre-ticked for nobody who set it.
            SendsContent = SendsContent && role == ProjectResourceRole.Instructions,
        };
    }

    // `role` parsed as `ProjectResourceRole`, or the safe fallback `Reference` for anything unrecognised —
    // missing, blank, mis-typed, or a role only a newer build knows (see the doc comment on `Role`).
    private static ProjectResourceRole _ParseRole(string? role) =>
        Enum.TryParse<ProjectResourceRole>(role, ignoreCase: true, out var parsed) ? parsed : ProjectResourceRole.Reference;
}
