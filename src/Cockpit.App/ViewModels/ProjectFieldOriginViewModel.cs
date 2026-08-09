namespace Cockpit.App.ViewModels;

// One `HostProjectField`'s resolved origin (AC-604) — what the ◆ Shared / ● This machine badge next to a
// project-editor field reads, and whether the control beside it is locked. Built once when the dialog opens
// (`ProjectDialogViewModel.CreateAsync`) from `ICockpitHost.GetProjectFieldOwnership`; nothing
// in the dialog changes a project's claim while it is open.
public sealed class ProjectFieldOriginViewModel
{
    // A field this project's registration leaves local — shown as "● This machine" when the project has any claim at all, and not shown otherwise (`ProjectDialogViewModel.HasFieldOwnership` gates that).
    public static readonly ProjectFieldOriginViewModel Local = new();

    public bool IsClaimed { get; private init; }

    // The negation of `IsClaimed` as its own property — a XAML class binding needs a direct bool path, not a negated one.
    public bool IsLocalOrigin => !IsClaimed;

    // Whether the control is locked — the negation of the registration's own
    // `Plugins.Abstractions.Projects.ProjectFieldOwnership.IsEditable` (AC-247). A claimed field with nowhere to
    // write an edit back to still locks (the default, `isEditable: false`); one whose source claimed it editable
    // unlocks, because `ProjectDialogViewModel.SaveAsync` now has a destination for that edit
    // (`ISharedProjectSource.WriteBackAsync`) instead of silently dropping it on save.
    public bool IsLockedHere { get; private init; }

    public string? SourceName { get; private init; }

    public string BadgeText => IsClaimed ? "◆ Shared" : "● This machine";

    // The reason shown under a locked field instead of leaving it silently disabled. Null when the field is not locked.
    public string? ReadOnlyReason => IsLockedHere ? $"Shared from {SourceName} — read-only here." : null;

    public static ProjectFieldOriginViewModel Claimed(string sourceName, bool isEditable) => new()
    {
        IsClaimed = true,
        IsLockedHere = !isEditable,
        SourceName = sourceName,
    };
}
