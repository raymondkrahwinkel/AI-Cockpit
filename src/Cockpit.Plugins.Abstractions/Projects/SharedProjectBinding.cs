namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// Enough of a <see cref="SharedProject"/>'s own portable definition (AC-246) for the host to build a usable
/// local <c>Project</c> at bind time — what <see cref="ISharedProjectSource.PrepareBindingAsync"/> returns.
/// </summary>
/// <remarks>
/// Deliberately thin and plugin-shape-agnostic: a plugin's own on-disk definition never crosses this boundary.
/// A one-time read, not a live handle — nothing here is kept in sync after the bind step that consumes it.
/// </remarks>
/// <param name="Name">
/// The project's display name, as the shared definition carries it right now.
/// </param>
public sealed record SharedProjectBinding(string Name)
{
    /// <summary>
    /// Free-text note on what this project is, same idiom as <c>Project.Description</c>. Null when the source has none.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The Git URL this project's source folder can be cloned from — an offer, not a requirement (AC-246: "Clone… is een aanbod, geen poort"). Null for a project with no source of its own.
    /// </summary>
    public string? GitUrl { get; init; }

    /// <summary>
    /// How the profile should behave here, the same idiom as <c>Project.BehaviorPrompt</c>. Null/blank appends nothing.
    /// </summary>
    public string? BehaviorPrompt { get; init; }

    /// <summary>
    /// Whether new sessions here isolate in their own git worktree by default, the same idiom as <c>Project.IsolateInWorktreeByDefault</c>.
    /// </summary>
    public bool IsolateInWorktreeByDefault { get; init; }

    /// <summary>
    /// Names of MCP servers this project's sessions start ticked, the same idiom as <c>ProjectMcpOverlay.EnabledServerNames</c>
    /// — null for a project that made no MCP choice at all, which starts every offered server ticked.
    /// </summary>
    public IReadOnlyList<string>? EnabledMcpServerNames { get; init; }

    /// <summary>
    /// The project's own resource rows (AC-246/AC-605), in the order the shared definition carries them.
    /// </summary>
    /// <remarks>
    /// This reader judges each row's <see cref="SharedProjectBindingResource.Reference"/> for itself rather than
    /// trusting the writer's classification blindly.
    /// </remarks>
    public IReadOnlyList<SharedProjectBindingResource> Resources { get; init; } = [];

    /// <summary>
    /// The source's own optimistic-concurrency token for the read this binding came from (AC-247) — null for a
    /// source with no write-back path.
    /// </summary>
    /// <remarks>
    /// A caller that goes on to edit and save carries this forward as
    /// <see cref="ISharedProjectSource.WriteBackAsync"/>'s <c>baseChecksum</c>, unmodified, for as long as the
    /// editor stays open.
    /// </remarks>
    public string? Checksum { get; init; }

    /// <summary>
    /// The shared logo's own PNG bytes (AC-763), already downloaded from the source's blob store — null when the
    /// shared definition names no logo, or a download was attempted and failed.
    /// </summary>
    public byte[]? LogoBytes { get; init; }
}

/// <summary>
/// One <see cref="SharedProjectBinding.Resources"/> row (AC-246) — <see cref="Role"/> a plain string, the same
/// "not an enum across this boundary" idiom <see cref="SharedProject.Role"/> already uses.
/// </summary>
/// <param name="Role">
/// What a session does with this row — matched case-insensitively against <c>Cockpit.Core.Projects.ProjectResourceRole</c>'s own names by whoever builds the local project; an unrecognised value is the caller's to fall back on, never this record's to guess at.
/// </param>
/// <param name="Reference">
/// Where this resource is, or what names it — exactly as the shared definition stores it, unresolved and unjudged.
/// <para>
/// AC-246: <b>blank on purpose</b> for a placeholder row — a machine-scope reference the writer's own definition
/// never carried past its own machine. A blank value here means "a row belongs here, its own machine's reference
/// does not travel; fill in yours" (<see cref="Label"/> still says what it is for) — never "skip this row".
/// </para>
/// </param>
public sealed record SharedProjectBindingResource(string Role, string Reference)
{
    /// <summary>
    /// What the operator who wrote this row called it. Null when they never named it.
    /// </summary>
    /// <remarks>
    /// AC-246: for a placeholder row, this is the one piece of the writer's own row that reaches every colleague
    /// even though <see cref="Reference"/> never does.
    /// </remarks>
    public string? Label { get; init; }
}

/// <summary>
/// What came of asking an <see cref="ISharedProjectSource"/> to prepare one project for binding (AC-246) — the
/// <see cref="SharedProjectListResult"/> idiom, once per project instead of once for a whole source.
/// </summary>
public sealed record SharedProjectBindingResult(bool Succeeded, SharedProjectBinding? Binding, string? Error)
{
    public static SharedProjectBindingResult Success(SharedProjectBinding binding) => new(true, binding, null);

    public static SharedProjectBindingResult Failed(string error) => new(false, null, error);
}
