namespace Cockpit.App.ViewModels;

// One `HostProjectField`'s resolved origin (AC-604) — what the ◆ Shared / ● This machine badge next to a project-editor
// field reads, and whether the control beside it is locked.
public sealed class ProjectFieldOriginViewModel
{
    // A field this project's registration leaves local — shown as "● This machine" when the project has any claim at all, and not shown otherwise (`ProjectDialogViewModel.HasFieldOwnership` gates that).
    public static readonly ProjectFieldOriginViewModel Local = new();

    public bool IsClaimed { get; private init; }

    // The negation of `IsClaimed` as its own property — a XAML class binding needs a direct bool path, not a negated one.
    public bool IsLocalOrigin => !IsClaimed;

    // A claimed field with nowhere to write an edit back to still locks (the default, `isEditable: false`); one whose
    // source claimed it editable unlocks, because `ProjectDialogViewModel.SaveAsync` now has a destination for that
    // edit (`ISharedProjectSource.WriteBackAsync`) instead of silently dropping it on save (AC-247).
    public bool IsLockedHere { get; private init; }

    public string? SourceName { get; private init; }

    // The operator's role on the claiming source (Viewer/Editor/Owner) — display only, same idiom as SourceName.
    public string? Role { get; private init; }

    public string BadgeText => IsClaimed ? "◆ Shared" : "● This machine";

    // The reason shown under a locked field instead of leaving it silently disabled — names the role (AC-248)
    // when the source reports one, so a Viewer sees why, not just that. Null when the field is not locked.
    public string? ReadOnlyReason => IsLockedHere
        ? Role is { Length: > 0 }
            ? $"Shared from {SourceName} — {Role} access is read-only here."
            : $"Shared from {SourceName} — read-only here."
        : null;

    public static ProjectFieldOriginViewModel Claimed(string sourceName, bool isEditable, string? role = null) => new()
    {
        IsClaimed = true,
        IsLockedHere = !isEditable,
        SourceName = sourceName,
        Role = role,
    };
}
