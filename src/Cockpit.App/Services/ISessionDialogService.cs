using Cockpit.App.ViewModels;

namespace Cockpit.App.Services;

/// <summary>
/// Shows the cockpit's modal dialogs from the view-model layer without the view models touching
/// window types (keeps <see cref="CockpitViewModel"/> unit-testable behind this seam).
/// </summary>
public interface ISessionDialogService
{
    /// <summary>Shows the New-session dialog and returns the confirmed choices, or null if cancelled.</summary>
    Task<NewSessionResult?> ShowNewSessionDialogAsync(SessionKind kind);

    /// <summary>Shows the Manage-profiles dialog on its own (e.g. from the sidebar), over the main window.</summary>
    Task ShowManageProfilesDialogAsync();
}
