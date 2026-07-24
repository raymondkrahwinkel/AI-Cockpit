namespace Cockpit.Core.Workspaces;

/// <summary>
/// What a workspace hosts. Three host types ship built in — <see cref="Sessions"/>, <see cref="Dashboard"/> and
/// <see cref="Projects"/> — and a plugin can register its own (<c>ICockpitHost.AddWorkspaceType</c>), each identified by a stable,
/// namespaced <see cref="Id"/>. A host type gates which <see cref="PaneKind"/>s may live in it, its "+"
/// affordance and its empty state; a plugin type owns its whole body instead and holds no grid panes. The type
/// is an invariant, fixed when the workspace is created.
/// </summary>
/// <remarks>
/// A value over an enum so the set is open: the host cannot enumerate the types a plugin will bring. The
/// original two host <see cref="Id"/>s are the same strings the enum used to serialize to (<c>"Sessions"</c>,
/// <c>"Dashboard"</c>), so a <c>cockpit.json</c> written before this change loads unchanged. Use
/// <see cref="FromId"/> when reading an id from disk so the host types keep matching case-insensitively as
/// the enum's <c>TryParse</c> did; a plugin id is treated as the API surface it is and matched exactly.
/// </remarks>
public readonly record struct WorkspaceType(string Id)
{
    /// <summary>Hosts AI sessions and plain terminals — the working context.</summary>
    public static WorkspaceType Sessions { get; } = new("Sessions");

    /// <summary>Hosts widget panes — the monitoring/at-a-glance context.</summary>
    public static WorkspaceType Dashboard { get; } = new("Dashboard");

    /// <summary>
    /// Hosts the projects overview (AC-162): what there is to work on, as cards each one Start away, with adding
    /// and editing alongside. Holds no panes of its own — like a plugin type it owns its whole surface, but built
    /// in, because what it starts is the host's.
    /// </summary>
    public static WorkspaceType Projects { get; } = new("Projects");

    /// <summary>Whether this is one of the built-in host types rather than a plugin-registered one.</summary>
    public bool IsBuiltIn => this == Sessions || this == Dashboard || this == Projects;

    /// <summary>
    /// The type for <paramref name="id"/>: one of the host types when it names one (case-insensitively, as
    /// the enum parsed), otherwise a plugin type carrying the id verbatim. A null or blank id falls back to
    /// <see cref="Sessions"/> — the same recovery the loader applied to an unparseable enum.
    /// </summary>
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
