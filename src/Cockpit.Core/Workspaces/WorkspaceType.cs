namespace Cockpit.Core.Workspaces;

// What a workspace hosts. Three host types ship built in — `Sessions`, `Dashboard` and
// `Projects` — and a plugin can register its own (`ICockpitHost.AddWorkspaceType`), each identified by a stable,
// namespaced `Id`. A host type gates which `PaneKind`s may live in it, its "+"
// affordance and its empty state; a plugin type owns its whole body instead and holds no grid panes. The type
// is an invariant, fixed when the workspace is created.
// A value over an enum so the set is open: the host cannot enumerate the types a plugin will bring. The
// original two host `Id`s are the same strings the enum used to serialize to (`"Sessions"`,
// `"Dashboard"`), so a `cockpit.json` written before this change loads unchanged. Use
// `FromId` when reading an id from disk so the host types keep matching case-insensitively as
// the enum's `TryParse` did; a plugin id is treated as the API surface it is and matched exactly.
public readonly record struct WorkspaceType(string Id)
{
    // Hosts AI sessions and plain terminals — the working context.
    public static WorkspaceType Sessions { get; } = new("Sessions");

    // Hosts widget panes — the monitoring/at-a-glance context.
    public static WorkspaceType Dashboard { get; } = new("Dashboard");

    // Hosts the projects overview (AC-162): what there is to work on, as cards each one Start away, with adding
    // and editing alongside. Holds no panes of its own — like a plugin type it owns its whole surface, but built
    // in, because what it starts is the host's.
    public static WorkspaceType Projects { get; } = new("Projects");

    // Whether this is one of the built-in host types rather than a plugin-registered one.
    public bool IsBuiltIn => this == Sessions || this == Dashboard || this == Projects;

    // The type for `id`: one of the host types when it names one (case-insensitively, as
    // the enum parsed), otherwise a plugin type carrying the id verbatim. A null or blank id falls back to
    // `Sessions` — the same recovery the loader applied to an unparseable enum.
    public static WorkspaceType FromId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Sessions;
        }

        if (string.Equals(id, Sessions.Id, StringComparison.OrdinalIgnoreCase))
        {
            return Sessions;
        }

        if (string.Equals(id, Dashboard.Id, StringComparison.OrdinalIgnoreCase))
        {
            return Dashboard;
        }

        return string.Equals(id, Projects.Id, StringComparison.OrdinalIgnoreCase) ? Projects : new WorkspaceType(id);
    }
}
