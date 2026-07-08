using Cockpit.App.ViewModels;

namespace Cockpit.App.Services;

/// <summary>
/// Shows the cockpit's modal dialogs from the view-model layer without the view models touching
/// window types (keeps <see cref="CockpitViewModel"/> unit-testable behind this seam).
/// </summary>
public interface ISessionDialogService
{
    /// <summary>
    /// Shows the New-session dialog — SDK vs TTY is chosen inside it (#32) — and returns the confirmed
    /// choices, or null if cancelled.
    /// </summary>
    Task<NewSessionResult?> ShowNewSessionDialogAsync();

    /// <summary>Shows the Manage-profiles dialog on its own (e.g. from the sidebar), over the main window.</summary>
    Task ShowManageProfilesDialogAsync();

    /// <summary>
    /// Shows the Options dialog (#13) over the main window, with <paramref name="viewModel"/> as its
    /// <see cref="Avalonia.Controls.Window.DataContext"/> so its tabs bind straight to the cockpit's
    /// existing option properties/commands.
    /// </summary>
    Task ShowOptionsDialogAsync(CockpitViewModel viewModel);
}
