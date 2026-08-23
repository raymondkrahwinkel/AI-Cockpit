namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// What came of asking an <see cref="ISharedProjectSource"/> for its projects (AC-245) — a whole-source failure,
/// distinct from one bad project among many, which a source is expected to leave out of <see cref="Projects"/>
/// rather than fail the whole call over.
/// </summary>
public sealed record SharedProjectListResult(bool Succeeded, IReadOnlyList<SharedProject> Projects, string? Error)
{
    public static SharedProjectListResult Success(IReadOnlyList<SharedProject> projects) => new(true, projects, null);

    public static SharedProjectListResult Failed(string error) => new(false, [], error);

    /// <summary>
    /// Projects this source can see the operator is a member of, but could not read enough of to confirm a
    /// portable definition for (AC-245) — a read attempt that failed for a reason that may be role-related
    /// rather than "not shared".
    /// </summary>
    /// <remarks>
    /// Deliberately not merged into <see cref="Projects"/>: a caller must choose what, if anything, to show for
    /// one of these. Empty by default; how the Projects workspace surfaces it is undecided.
    /// </remarks>
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
