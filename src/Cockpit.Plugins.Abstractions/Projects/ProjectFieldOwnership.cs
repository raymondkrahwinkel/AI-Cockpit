namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// Who owns a claimed <see cref="HostProjectField"/> and whether the project editor still lets the operator
/// change it here (AC-604) — what draws the ◆ Shared / ● This machine badge and, when not editable, locks the
/// control and shows why instead of leaving it silently disabled.
/// </summary>
/// <param name="SourceName">Shown as the badge's origin — "Depot — Work". Keep it short: it is a tooltip caption, not a sentence.</param>
/// <param name="IsEditable">
/// The intended contract: whether the operator can change this field here. The host does not honour <see langword="true"/>
/// yet — every claimed field renders locked regardless, because there is no write-back destination for an edit
/// until AC-247 (an editable-but-nowhere-to-save control would drop what the operator typed silently). Set it
/// for forward compatibility; do not rely on it unlocking anything today.
/// </param>
public sealed record ProjectFieldOwnership(string SourceName, bool IsEditable = false);
