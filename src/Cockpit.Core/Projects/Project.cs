namespace Cockpit.Core.Projects;

/// <summary>
/// What a session works on (AC-158): the source folder, which MCP servers are on, the profile it starts under
/// and whether its sessions isolate in a worktree. A session is profile × project — the profile stays who and
/// how you work (provider, model, credentials), the project says what you work on. Without it, working on a
/// second codebase meant a second near-identical profile.
/// <para>
/// A project <em>uses</em> a profile, it never extends one: it names one by label and overrides or supplements
/// what that profile defaults to (<see cref="Cockpit.Core.Sessions.SessionStartDefaults"/> is the only place the
/// two meet). A profile knows nothing about projects and keeps working without one.
/// </para>
/// </summary>
/// <param name="Id">Stable id, referenced by a session and never shown.</param>
/// <param name="Name">The project's display name — renamable, and free to collide with another project's name.</param>
public sealed record Project(string Id, string Name)
{
    /// <summary>Free-text note on what this project is, shown under its name in the launcher and the manager.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// The folder its sessions start in. Null/blank for a project with no source of its own — an administrative
    /// project is a perfectly good project, and this model is not only for repositories.
    /// </summary>
    public string? SourceDirectory { get; init; }

    /// <summary>The Git URL <see cref="SourceDirectory"/> was cloned from (AC-90), so the manager can show where it came from. Null when the folder was picked rather than cloned.</summary>
    public string? GitUrl { get; init; }

    /// <summary>
    /// The profile its sessions start under, matched by label the way <c>NewSessionPrefill.ProfileLabel</c> is —
    /// deliberately a label and not a profile: a project points at a profile, it does not own or extend one. A
    /// label matching no profile leaves the dialog on its own default rather than failing the start.
    /// </summary>
    public string? DefaultProfileLabel { get; init; }

    /// <summary>
    /// How the profile should behave here, appended to the session's system prompt (the AC-180 seam) rather than
    /// replacing anything the profile says. This is the override idea at its plainest: the same profile works
    /// differently per project without a second profile existing. Null/blank appends nothing.
    /// </summary>
    public string? BehaviorPrompt { get; init; }

    /// <summary>
    /// Whether new sessions here isolate in their own git worktree (AC-85) when <see cref="SourceDirectory"/> is
    /// a repository. A default only: worktree stays a per-session choice, still overridable in the dialog.
    /// </summary>
    public bool IsolateInWorktreeByDefault { get; init; }

    /// <summary>Which MCP servers its sessions see, as a change on top of the global registry rather than a list of its own — see <see cref="ProjectMcpOverlay"/>.</summary>
    public ProjectMcpOverlay McpOverlay { get; init; } = ProjectMcpOverlay.None;

    /// <summary>
    /// Which knowledge store its sessions get (the Depot plugin's, today). Carried but not yet read: memory is a
    /// plugin contribution (AC-165/166), and the host stores the reference so v2 only has to consume it.
    /// </summary>
    public string? MemoryRef { get; init; }

    /// <summary>A new project with a generated id, mirroring <c>Workspace.Create</c>.</summary>
    public static Project Create(string name) => new(Guid.NewGuid().ToString("n"), name);
}
