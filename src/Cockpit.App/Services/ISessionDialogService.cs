using Cockpit.App.ViewModels;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Services;

/// <summary>
/// Shows the cockpit's modal dialogs from the view-model layer without the view models touching
/// window types (keeps <see cref="CockpitViewModel"/> unit-testable behind this seam).
/// </summary>
public interface ISessionDialogService
{
    /// <summary>
    /// Shows the New-session dialog — SDK vs TTY is chosen inside it (#32) — and returns the confirmed
    /// choices, or null if cancelled. <paramref name="prefill"/> (#AC-96) seeds the dialog's fields — a profile
    /// by label, a working directory, a session name, a resume id — so a caller that knows some of them offers
    /// them ready while the operator still confirms and can change every one; null opens it on its own defaults.
    /// <paramref name="isolateInWorktree"/> additionally turns worktree isolation on for the pre-filled folder —
    /// the AC-85 reattach case (starting a session in an existing worktree so starting re-owns it), separate from
    /// <paramref name="prefill"/> because it is a host reattach concern, not one of the plugin-facing prefill fields.
    /// <paramref name="project"/> opens the dialog on that project (AC-164), so its folder, profile, worktree default
    /// and MCP overlay apply exactly as if the operator had picked it there — a host concern too, and not a prefill
    /// field: a project is a thing the dialog knows, while a prefill is a set of values a plugin hands in.
    /// </summary>
    Task<NewSessionResult?> ShowNewSessionDialogAsync(NewSessionPrefill? prefill = null, bool isolateInWorktree = false, Project? project = null);

    /// <summary>
    /// Opens the managed-worktrees dialog (AC-85): the git worktrees the cockpit created, their state and owner, with
    /// reattach and remove. Takes <paramref name="worktrees"/> as a parameter rather than injecting it (like
    /// <see cref="ShowOptionsDialogAsync"/>) so the dialog service does not depend on the view model that itself
    /// depends on the dialog service for the remove-consent prompt.
    /// </summary>
    Task ShowWorktreesDialogAsync(WorktreesViewModel worktrees);

    /// <summary>Shows the Manage-profiles dialog on its own (e.g. from the sidebar), over the main window.</summary>
    Task ShowManageProfilesDialogAsync();

    /// <summary>
    /// Shows the project editor (AC-160) for <paramref name="project"/>, or for a new project when it is null,
    /// and returns what the operator saved — null when they cancelled. Persisting is the caller's: this hands
    /// back an edited value the same way the New-session dialog hands back its choices.
    /// </summary>
    Task<Project?> ShowProjectDialogAsync(Project? project);

    /// <summary>Shows the MCP-servers dialog (#26), over the main window, for editing the shared MCP-server registry.</summary>
    Task ShowMcpServersDialogAsync();

    /// <summary>Shows the Verify-runners dialog (AC-86), over the main window, for registering the per-project command the visual verify loop may run.</summary>
    Task ShowVerifyRunnersDialogAsync();

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

    /// <summary>Opens a folder picker for choosing a local plugin store's folder (AC-7); returns the chosen path or null if cancelled.</summary>
    Task<string?> PickPluginStoreFolderAsync();

    /// <summary>Picks a dashboard file to import; returns the chosen path or null if cancelled.</summary>
    Task<string?> PickDashboardToImportAsync();

    /// <summary>Picks where to write a dashboard, offering <paramref name="suggestedName"/> as the file name; returns the chosen path or null if cancelled.</summary>
    Task<string?> PickDashboardExportPathAsync(string suggestedName);

    /// <summary>Shows the first-load plugin consent dialog (#14); returns true only when the operator explicitly enables the plugin.</summary>
    Task<bool> ShowPluginConsentAsync(PluginConsentInfo info);

    /// <summary>Shows the About dialog (#46) over the main window: app name, version, description and links.</summary>
    Task ShowAboutDialogAsync();

    /// <summary>Opens the delegated-tasks view (#67), so work another session handed to a profile stays visible and stoppable.</summary>
    Task ShowDelegatedTasksDialogAsync();

    /// <summary>Shows the command palette (#: command palette) over the given commands; runs the chosen one after the palette closes.</summary>
    Task ShowCommandPaletteDialogAsync(IReadOnlyList<PaletteCommand> commands);

    /// <summary>Asks the operator to confirm a destructive action (remove a store/profile/plugin/…). Returns true only when they confirm; Cancel/✕/Esc return false. Shown over the topmost window.</summary>
    Task<bool> ShowConfirmationDialogAsync(string title, string message, string confirmLabel = "Remove");

    /// <summary>
    /// Shows the Set-status dialog (AC-32) seeded with <paramref name="currentStatusline"/> so the operator can edit a
    /// session's status line by hand. Returns the new value — an empty string when they clear it — or null when they
    /// cancel, leaving the status unchanged.
    /// </summary>
    Task<string?> ShowSetStatusDialogAsync(string currentStatusline);
}
