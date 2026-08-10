namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// Who owns a claimed <see cref="HostProjectField"/> and whether the project editor still lets the operator
/// change it here (AC-604) — what draws the ◆ Shared / ● This machine badge and, when not editable, locks the
/// control and shows why instead of leaving it silently disabled.
/// </summary>
/// <param name="SourceName">Shown as the badge's origin — "Depot — Work". Keep it short: it is a tooltip caption, not a sentence.</param>
/// <param name="IsEditable">
/// Whether the operator can change this field here. <see langword="false"/> (the default) locks the control and
/// shows why instead of leaving it silently disabled. <see langword="true"/> unlocks it — set this only once the
/// claiming plugin's own <see cref="ISharedProjectSource.WriteBackAsync"/> can actually take an edit to this
/// field (AC-247); an editable-but-nowhere-to-save control would drop what the operator typed silently on save.
/// </param>
/// <param name="Role">
/// The operator's <see cref="SharedProject.Role"/> on the source this field came from — Viewer/Editor/Owner, or
/// null when the source cannot say. Display only, same as <see cref="SharedProject.Role"/> itself: it names why a
/// locked field is locked (AC-248) rather than leaving "read-only" unexplained.
/// </param>
public sealed record ProjectFieldOwnership(string SourceName, bool IsEditable = false, string? Role = null);
