namespace Cockpit.Core.Workspaces;

// What a workspace hosts: `Sessions`, `Dashboard`, `Projects` built in, plus plugin-registered types.
// AC-1013: trimmed — a value over an enum so the set stays open to plugins; `Id`s match the old enum's
// serialized strings for back-compat, `FromId` matches built-ins case-insensitively — see ticket.
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
