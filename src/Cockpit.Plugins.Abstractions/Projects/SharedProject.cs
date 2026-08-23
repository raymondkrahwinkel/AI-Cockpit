namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// One project a plugin's <see cref="ISharedProjectSource"/> can see but this machine has not bound yet (AC-245) —
/// the entry the Projects workspace lists under "Shared via …" beside the local projects, until a later binding
/// step (AC-246) turns it into an ordinary local <c>Project</c> here.
/// </summary>
/// <param name="Id">
/// The reference a local project would carry once bound — the same shape <c>Project.MemoryRef</c> already uses
/// (<c>&lt;scheme&gt;:&lt;value&gt;</c>), so a project editor's picker and this catalog agree on what "the same
/// project" means without a second identifier to keep in sync. What the host cross-references against every local
/// project's own memory reference to know this one is already bound and should not be listed twice.
/// </param>
/// <param name="Name">
/// The project's display name, read from wherever the source keeps its portable definition — never copied into <c>cockpit.json</c>.
/// </param>
public sealed record SharedProject(string Id, string Name)
{
    /// <summary>
    /// Free-text note on what this project is, same idiom as <c>Project.Description</c>. Null when the source has none.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The operator's membership on this project, shown as-is next to it (Viewer/Editor/Owner) — display only.
    /// Null when the source cannot say. Not read for any access decision here; a source that gates writes on it
    /// does so on its own terms.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>
    /// Whether this operator's role on this project allows <see cref="ISharedProjectSource.WriteBackAsync"/> to
    /// succeed (AC-247) — a plain bool rather than <see cref="Role"/> itself, so a Viewer's fields render locked
    /// before they ever type an edit that would only be rejected later.
    /// </summary>
    /// <remarks>
    /// Not itself an access decision — the source's own <see cref="ISharedProjectSource.WriteBackAsync"/> still
    /// enforces the real rule. False by default.
    /// </remarks>
    public bool CanWriteBack { get; init; }
}
