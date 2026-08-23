namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// What came of asking an <see cref="ISharedProjectSource"/> for its projects (AC-245) — a whole-source failure
/// (not signed in, unreachable, timed out), distinct from one bad project among many, which a source is expected to
/// leave out of <see cref="Projects"/> rather than fail the whole call over (a Depot project with no
/// <c>.cockpit/project.json</c>, say — not every project on a connection opts into being shared this way).
/// </summary>
public sealed record SharedProjectListResult(bool Succeeded, IReadOnlyList<SharedProject> Projects, string? Error)
{
    public static SharedProjectListResult Success(IReadOnlyList<SharedProject> projects) => new(true, projects, null);

    public static SharedProjectListResult Failed(string error) => new(false, [], error);

    /// <summary>
    /// Projects this source can see the operator is a member of, but could not read enough of to confirm a
    /// portable definition for (AC-245) — a role visible in a membership listing whose read attempt failed for a
    /// reason that may be role-related rather than "not shared" (Depot's own MCP <c>read</c> tool requires at least
    /// Editor today while <c>list_projects</c> is ungated, measured against <c>ProjectMemberAccessGuard</c>;
    /// intended to change so a Viewer gets read access too — Raymond, 2026-08-02 — which is why this is a named,
    /// visible degradation and not a silent one: once that ships, these simply stop appearing here with no
    /// consumer-side change needed). Deliberately not merged into <see cref="Projects"/>: a caller must choose
    /// what, if anything, to show for one of these rather than silently treating it the same as a project this
    /// source actually confirmed. Empty by default; nothing in this SDK consumes it yet — how (or whether) the
    /// Projects workspace surfaces it is undecided.
    /// </summary>
    public IReadOnlyList<UnreadableSharedProject> VisibleButUnreadable { get; init; } = [];
}

/// <summary>
/// See <see cref="SharedProjectListResult.VisibleButUnreadable"/>.
/// </summary>
/// <param name="Id">
/// Same shape as <see cref="SharedProject.Id"/>.
/// </param>
/// <param name="Name">
/// Best-effort only — the source's own name for it, not a confirmed portable one (that read is exactly what this state means could not happen).
/// </param>
/// <param name="Role">
/// The operator's membership role, same idiom as <see cref="SharedProject.Role"/>.
/// </param>
public sealed record UnreadableSharedProject(string Id, string Name, string? Role);
