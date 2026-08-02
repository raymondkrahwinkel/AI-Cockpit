namespace Cockpit.App.ViewModels;

/// <summary>
/// One <c>HostProjectField</c>'s resolved origin (AC-604) — what the ◆ Shared / ● This machine badge next to a
/// project-editor field reads, and whether the control beside it is locked. Built once when the dialog opens
/// (<see cref="ProjectDialogViewModel.CreateAsync"/>) from <c>ICockpitHost.GetProjectFieldOwnership</c>; nothing
/// in the dialog changes a project's claim while it is open.
/// </summary>
public sealed class ProjectFieldOriginViewModel
{
    /// <summary>A field this project's registration leaves local — shown as "● This machine" when the project has any claim at all, and not shown otherwise (<see cref="ProjectDialogViewModel.HasFieldOwnership"/> gates that).</summary>
    public static readonly ProjectFieldOriginViewModel Local = new();

    public bool IsClaimed { get; private init; }

    /// <summary>The negation of <see cref="IsClaimed"/> as its own property — a XAML class binding needs a direct bool path, not a negated one.</summary>
    public bool IsLocalOrigin => !IsClaimed;

    /// <summary>
    /// Whether the control is locked. Every claimed field is locked, regardless of
    /// <see cref="Plugins.Abstractions.Projects.ProjectFieldOwnership.IsEditable"/> — there is no write-back
    /// destination for an edit yet (AC-247), so a control that let the operator type would accept a value that
    /// disappears silently on save. Reconsider this once AC-247 gives an editable claim somewhere to write to.
    /// </summary>
    public bool IsLockedHere { get; private init; }

    public string? SourceName { get; private init; }

    public string BadgeText => IsClaimed ? "◆ Shared" : "● This machine";

    /// <summary>The reason shown under a locked field instead of leaving it silently disabled. Null when the field is not locked.</summary>
    public string? ReadOnlyReason => IsLockedHere ? $"Shared from {SourceName} — read-only here." : null;

    /// <param name="isEditable">
    /// Carried from the registration for when AC-247 adds a write-back path; not read here today — see
    /// <see cref="IsLockedHere"/>.
    /// </param>
    public static ProjectFieldOriginViewModel Claimed(string sourceName, bool isEditable) => new()
    {
        IsClaimed = true,
        IsLockedHere = true,
        SourceName = sourceName,
    };
}
