using Cockpit.App.ViewModels;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Services;

/// <summary>
/// Shows the cockpit's dialogs from the view-model layer without the view models touching
/// window types (keeps <see cref="CockpitViewModel"/> unit-testable behind this seam).
/// </summary>
public interface ISessionDialogService
{
    /// <summary>
    /// Shows the New-session dialog — SDK vs TTY chosen inside it (#32) — returning the confirmed choices, or null
    /// if cancelled. <paramref name="prefill"/> (#AC-96) seeds fields; <paramref name="isolateInWorktree"/> turns on
    /// worktree isolation (AC-85 reattach); <paramref name="project"/> opens on that project (AC-164).
    /// </summary>
    Task<NewSessionResult?> ShowNewSessionDialogAsync(NewSessionPrefill? prefill = null, bool isolateInWorktree = false, Project? project = null);

    /// <summary>
    /// Opens the managed-worktrees dialog (AC-85): the git worktrees the cockpit created, with reattach and remove.
    /// Takes <paramref name="worktrees"/> as a parameter rather than injecting it, avoiding a circular dependency
    /// with the view model this service serves for the remove-consent prompt.
    /// </summary>
    Task ShowWorktreesDialogAsync(WorktreesViewModel worktrees);

    /// <summary>
    /// Shows the assistant's own profile editor (Options → Voice) — its own dialog, since that record is not a
    /// session profile. <paramref name="assistant"/> backs the restart button; a parameter, not injected, for the
    /// same reason as <see cref="ShowWorktreesDialogAsync"/>. Null just offers no restart.
    /// </summary>
    Task ShowAssistantProfileDialogAsync(IAssistantSessionHost? assistant);

    /// <summary>
    /// Shows the projects manager (AC-161): saved projects, with add, edit and remove. Its own dialog rather than a
    /// tab in Options (Raymond, 2026-07-24). Takes <paramref name="projects"/> as a parameter for the same reason
    /// <see cref="ShowWorktreesDialogAsync"/> does: injecting it would be a circle.
    /// </summary>
    Task ShowProjectsDialogAsync(ProjectsViewModel projects);

    /// <summary>
    /// Shows the project editor (AC-160) for <paramref name="project"/>, or a new one when null; returns what was
    /// saved, or null if cancelled (persisting is the caller's). <paramref name="sharedSource"/> (AC-247), when
    /// set, reads a fresh <see cref="SharedProjectBinding"/> first so Save writes a claimed field back correctly.
    /// </summary>
    Task<Project?> ShowProjectDialogAsync(Project? project, ISharedProjectSource? sharedSource = null);

    /// <summary>
    /// Shows the "Finish setting up…" bind step (AC-246) for <paramref name="sharedProject"/>, not yet bound on this
    /// machine. Reads the full definition through <paramref name="source"/> first; returns null both on cancel and
    /// on a failed read (shown as an error either way). Persisting the result is the caller's.
    /// </summary>
    Task<Project?> ShowSharedProjectBindingDialogAsync(SharedProject sharedProject, string sourceName, ISharedProjectSource source);

    /// <summary>
    /// Shows AC-620's confirmation screen for publishing <paramref name="project"/> — a local project not yet bound
    /// — to one of <paramref name="publishSources"/> (registered sources with <see cref="ISharedProjectSource.CanPublish"/>).
    /// Returns <paramref name="project"/> with its new binding row on success, or null when cancelled.
    /// </summary>
    Task<Project?> ShowShareProjectDialogAsync(Project project, IReadOnlyList<ISharedProjectSource> publishSources);

    /// <summary>
    /// Shows the Verify-runners dialog (AC-86), over the main window, for registering the per-project command the visual verify loop may run.
    /// </summary>
    Task ShowVerifyRunnersDialogAsync();

    /// <summary>
    /// Shows the plugin store dialog (#62) over the currently active window — a browsing layer around
    /// <paramref name="manager"/>, the same <see cref="PluginManagerViewModel"/> instance Options→Plugins uses.
    /// <paramref name="initialFilter"/> preselects a sidebar scope (#65); null keeps the default Discover page.
    /// </summary>
    Task ShowPluginStoreDialogAsync(PluginManagerViewModel manager, PluginStoreFilter? initialFilter = null);

    /// <summary>
    /// Shows the Options dialog (#13) over the main window, with <paramref name="viewModel"/> as its
    /// <see cref="Avalonia.Controls.Window.DataContext"/>. <paramref name="category"/> is the nav item's
    /// <c>Tag</c> to open on, or null for the default — AC-1001's deep-link actions pass this instead of a new window.
    /// </summary>
    Task ShowOptionsDialogAsync(CockpitViewModel viewModel, string? category = null);

    /// <summary>
    /// Opens a file picker filtered to <c>.zip</c> archives for installing a plugin (#14); returns the chosen path or null if cancelled.
    /// </summary>
    Task<string?> PickPluginZipAsync();

    /// <summary>
    /// Opens a folder picker for choosing a local plugin store's folder (AC-7); returns the chosen path or null if cancelled.
    /// </summary>
    Task<string?> PickPluginStoreFolderAsync();

    /// <summary>
    /// Picks a dashboard file to import; returns the chosen path or null if cancelled.
    /// </summary>
    Task<string?> PickDashboardToImportAsync();

    /// <summary>
    /// Picks where to write a dashboard, offering <paramref name="suggestedName"/> as the file name; returns the chosen path or null if cancelled.
    /// </summary>
    Task<string?> PickDashboardExportPathAsync(string suggestedName);

    /// <summary>
    /// Shows the first-load plugin consent dialog (#14); returns true only when the operator explicitly enables the plugin.
    /// </summary>
    Task<bool> ShowPluginConsentAsync(PluginConsentInfo info);

    /// <summary>
    /// Shows the About dialog (#46) over the main window: app name, version, description and links.
    /// </summary>
    Task ShowAboutDialogAsync();

    /// <summary>
    /// Shows the in-app glossary (AC-512) over the main window: the five primitives, explained without a browser.
    /// </summary>
    Task ShowGlossaryDialogAsync();

    /// <summary>
    /// Opens the delegated-tasks view (#67), so work another session handed to a profile stays visible and stoppable.
    /// </summary>
    Task ShowDelegatedTasksDialogAsync();

    /// <summary>
    /// Opens the read-only window on the agent line (AC-397): what agents on this desk said, wakes asked for,
    /// what's claimed and refused. Takes its view model like the worktrees dialog does, since the caller owns it.
    /// </summary>
    Task ShowAgentLineInspectorDialogAsync(AgentLineInspectorViewModel inspector);

    /// <summary>
    /// Shows the command palette (#: command palette) over the given commands; runs the chosen one after the palette closes.
    /// </summary>
    Task ShowCommandPaletteDialogAsync(IReadOnlyList<PaletteCommand> commands);

    /// <summary>
    /// Asks the operator to confirm a destructive action (remove a store/profile/plugin/…). Returns true only when they confirm; Cancel/✕/Esc return false. Shown over the topmost window.
    /// </summary>
    Task<bool> ShowConfirmationDialogAsync(string title, string message, string confirmLabel = "Remove");

    /// <summary>
    /// Shows the Set-status dialog (AC-32) seeded with <paramref name="currentStatusline"/>. Returns the new value
    /// — empty when cleared — or null when cancelled, leaving the status unchanged.
    /// </summary>
    Task<string?> ShowSetStatusDialogAsync(string currentStatusline);

    /// <summary>
    /// Asks for a moment and a prompt to pick a session up with (AC-231), starting from <paramref name="suggested"/>.
    /// Null when the operator backed out.
    /// </summary>
    Task<(DateTimeOffset Moment, string Prompt)?> ShowScheduleResumeDialogAsync(DateTimeOffset suggested, string prompt);
}
