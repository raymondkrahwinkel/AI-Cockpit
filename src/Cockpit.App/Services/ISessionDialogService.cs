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

    /// <summary>Shows the MCP-servers dialog (#26), over the main window, for editing the shared MCP-server registry.</summary>
    Task ShowMcpServersDialogAsync();

    /// <summary>
    /// Shows the plugin store dialog (#62) over the currently active window (typically the Options dialog
    /// it was opened from, so it centers over the dialog stack rather than jumping behind it to the main
    /// window) — a browsing/presentation layer around <paramref name="manager"/>, the same
    /// <see cref="PluginManagerViewModel"/> instance the Options→Plugins tab uses. Every install/update,
    /// the consent step and the restart banner go through that shared instance unchanged.
    /// <paramref name="initialFilter"/> preselects a sidebar scope (#65: a plugin-update toast's action
    /// opens straight onto <see cref="PluginStoreFilter.UpdatesAvailable"/>); null keeps the default
    /// Discover page.
    /// </summary>
    Task ShowPluginStoreDialogAsync(PluginManagerViewModel manager, PluginStoreFilter? initialFilter = null);

    /// <summary>
    /// Shows the Options dialog (#13) over the main window, with <paramref name="viewModel"/> as its
    /// <see cref="Avalonia.Controls.Window.DataContext"/> so its tabs bind straight to the cockpit's
    /// existing option properties/commands. <paramref name="selectPluginsTab"/> opens straight to the
    /// Plugins tab instead of the default first tab.
    /// </summary>
    Task ShowOptionsDialogAsync(CockpitViewModel viewModel, bool selectPluginsTab = false);

    /// <summary>Opens a file picker filtered to <c>.zip</c> archives for installing a plugin (#14); returns the chosen path or null if cancelled.</summary>
    Task<string?> PickPluginZipAsync();

    /// <summary>Shows the first-load plugin consent dialog (#14); returns true only when the operator explicitly enables the plugin.</summary>
    Task<bool> ShowPluginConsentAsync(PluginConsentInfo info);

    /// <summary>Shows the About dialog (#46) over the main window: app name, version, description and links.</summary>
    Task ShowAboutDialogAsync();
}
