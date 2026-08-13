namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// Enough of a <see cref="SharedProject"/>'s own portable definition (AC-246) for the host to build a usable local
/// <c>Project</c> at bind time — what <see cref="ISharedProjectSource.PrepareBindingAsync"/> returns. Deliberately
/// thin and plugin-shape-agnostic: a plugin's own on-disk definition (Depot's <c>CockpitProjectDefinition</c>, say)
/// never crosses this boundary, the same reason <see cref="SharedProject"/> itself carries plain fields rather than
/// a plugin type.
/// <para>
/// A one-time read, not a live handle: nothing here is kept in sync after the bind step that consumes it. The local
/// <c>Project</c> built from it is an ordinary project from then on — see <see cref="ISharedProjectSource.PrepareBindingAsync"/>'s
/// own remarks on why staying current afterward is a different concern (AC-247's read-and-conflict path) from this
/// one (AC-246's "make it usable at all").
/// </para>
/// </summary>
/// <param name="Name">The project's display name, as the shared definition carries it right now.</param>
public sealed record SharedProjectBinding(string Name)
{
    /// <summary>Free-text note on what this project is, same idiom as <c>Project.Description</c>. Null when the source has none.</summary>
    public string? Description { get; init; }

    /// <summary>The Git URL this project's source folder can be cloned from — an offer, not a requirement (AC-246: "Clone… is een aanbod, geen poort"). Null for a project with no source of its own.</summary>
    public string? GitUrl { get; init; }

    /// <summary>How the profile should behave here, the same idiom as <c>Project.BehaviorPrompt</c>. Null/blank appends nothing.</summary>
    public string? BehaviorPrompt { get; init; }

    /// <summary>Whether new sessions here isolate in their own git worktree by default, the same idiom as <c>Project.IsolateInWorktreeByDefault</c>.</summary>
    public bool IsolateInWorktreeByDefault { get; init; }

    /// <summary>
    /// Names of MCP servers this project's sessions start ticked, the same idiom as <c>ProjectMcpOverlay.EnabledServerNames</c>
    /// — null for a project that made no MCP choice at all, which starts every offered server ticked.
    /// </summary>
    public IReadOnlyList<string>? EnabledMcpServerNames { get; init; }

    /// <summary>
    /// The project's own resource rows (AC-246/AC-605), in the order the shared definition carries them. Every row
    /// here already made it into the shared definition, so by construction none of them is machine-specific in the
    /// writer's own classification — but this reader judges each row's <see cref="SharedProjectBindingResource.Reference"/>
    /// for itself (see the binding dialog) rather than trusting that upstream guarantee blindly: a hand-edited
    /// definition, or a future, less strict writer, is not this reader's problem to assume away.
    /// </summary>
    public IReadOnlyList<SharedProjectBindingResource> Resources { get; init; } = [];

    /// <summary>
    /// The source's own optimistic-concurrency token for the read this binding came from (AC-247) — null for a
    /// source with no write-back path, or for the original AC-246 bind-time read this field did not exist for yet.
    /// A caller that goes on to edit and save this project's claimed fields carries this forward as
    /// <see cref="ISharedProjectSource.WriteBackAsync"/>'s <c>baseChecksum</c>, unmodified, for as long as the
    /// editor stays open on the values this read produced — never refreshed quietly out from under an
    /// in-progress edit, which is exactly what would defeat the point of an optimistic-concurrency token.
    /// </summary>
    public string? Checksum { get; init; }

    /// <summary>
    /// The shared logo's own PNG bytes (AC-763), already downloaded from the source's blob store — null when the
    /// shared definition names no logo, or a source that has no logo of its own at all. Also null when a download
    /// was attempted and failed: a logo is decoration, the same "costs the picture, not the whole bind" rule
    /// <c>Cockpit.Infrastructure.Projects.ProjectLogoStore.SaveAsync</c> already follows for a local one.
    /// </summary>
    public byte[]? LogoBytes { get; init; }
}

/// <summary>One <see cref="SharedProjectBinding.Resources"/> row (AC-246) — <see cref="Role"/> a plain string, the same "not an enum across this boundary" idiom <see cref="SharedProject.Role"/> already uses.</summary>
/// <param name="Role">What a session does with this row — matched case-insensitively against <c>Cockpit.Core.Projects.ProjectResourceRole</c>'s own names by whoever builds the local project; an unrecognised value is the caller's to fall back on, never this record's to guess at.</param>
/// <param name="Reference">
/// Where this resource is, or what names it — exactly as the shared definition stores it, unresolved and unjudged.
/// <para>
/// AC-246 (Raymond, 2026-08-02): <b>blank on purpose</b> for a placeholder row — a machine-scope reference the
/// writer's own definition never carried past its own machine (<c>Cockpit.Plugin.Depot.ProjectDefinition.CockpitProjectResourceEntry.Placeholder</c>).
/// A blank value here is never "nothing to show" the way it would be for an ordinary row — it means "a row belongs
/// here, its own machine's reference does not travel; fill in yours" (<see cref="Label"/> still says what it is
/// for). This is the one place in the whole pipeline that distinction has to be read correctly: a caller that
/// treats a blank <see cref="Reference"/> as "skip this row" here would silently drop exactly the rows this ticket
/// exists to ask about.
/// </para>
/// </param>
public sealed record SharedProjectBindingResource(string Role, string Reference)
{
    /// <summary>
    /// What the operator who wrote this row called it. Null when they never named it.
    /// <para>
    /// AC-246: for a placeholder row, this is the one piece of the writer's own row that <em>does</em> reach every
    /// colleague even though <see cref="Reference"/> never does — Raymond accepted that price explicitly
    /// (2026-08-02): a label like <c>"Productie-DB"</c> already says plenty about what the row is for. A row
    /// <see cref="ProjectResourceSecretPathHeuristic"/>-shaped enough to be secret never reaches here at all —
    /// that gate drops the label along with the reference, unlike the plain machine-scope case this remark is
    /// about.
    /// </para>
    /// </summary>
    public string? Label { get; init; }
}

/// <summary>What came of asking an <see cref="ISharedProjectSource"/> to prepare one project for binding (AC-246) — the <see cref="SharedProjectListResult"/> idiom, once per project instead of once for a whole source.</summary>
public sealed record SharedProjectBindingResult(bool Succeeded, SharedProjectBinding? Binding, string? Error)
{
    public static SharedProjectBindingResult Success(SharedProjectBinding binding) => new(true, binding, null);

    public static SharedProjectBindingResult Failed(string error) => new(false, null, error);
}
