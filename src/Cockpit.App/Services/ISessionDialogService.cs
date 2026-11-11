using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Projects;
using Cockpit.Core.Updates;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Services;

/// <summary>
/// Shows the cockpit's dialogs from the view-model layer without the view models touching
/// window types (keeps <see cref="CockpitViewModel"/> unit-testable behind this seam).
/// </summary>
public interface ISessionDialogService : IProjectDialogs, IPluginManagerDialogs
{
    new Task<string?> PickFolderAsync(string title);
    new Task<bool> ShowConfirmationDialogAsync(string title, string message, string confirmLabel = "Remove");

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
    /// Shows the assistant's own profile editor (Options → Assistant) — its own dialog, since that record is not a
    /// session profile. <paramref name="assistant"/> backs the restart button; a parameter, not injected, for the
    /// same reason as <see cref="ShowWorktreesDialogAsync"/>. Null just offers no restart.
    /// </summary>
    Task ShowAssistantProfileDialogAsync(IAssistantSessionHost? assistant, AssistantProfileSetup? setup = null);

    /// <summary>
    /// Shows the Verify-runners dialog (AC-86), over the main window, for registering the per-project command the visual verify loop may run.
    /// </summary>
    Task ShowVerifyRunnersDialogAsync();

    /// <summary>
    /// Shows the Options dialog (#13) over the main window, with <paramref name="viewModel"/> as its
    /// <see cref="Avalonia.Controls.Window.DataContext"/>. <paramref name="category"/> is the nav item's
    /// <c>Tag</c> to open on, or null for the default — AC-1001's deep-link actions pass this instead of a new window.
    /// </summary>
    Task ShowOptionsDialogAsync(CockpitViewModel viewModel, string? category = null);

    /// <summary>
    /// Picks a dashboard file to import; returns the chosen path or null if cancelled.
    /// </summary>
    Task<string?> PickDashboardToImportAsync();

    /// <summary>
    /// Picks where to write a dashboard, offering <paramref name="suggestedName"/> as the file name; returns the chosen path or null if cancelled.
    /// </summary>
    Task<string?> PickDashboardExportPathAsync(string suggestedName);

    /// <summary>
    /// Shows the About dialog (#46) over the main window: app name, version, description and links.
    /// </summary>
    Task ShowAboutDialogAsync();

    /// <summary>
    /// Shows the in-app glossary (AC-512) over the main window: the five primitives, explained without a browser.
    /// </summary>
    Task ShowGlossaryDialogAsync();

    /// <summary>
    /// Shows what an update brings (AC-1515): the changelog entries after <paramref name="current"/> up to and including
    /// <paramref name="offered"/>. Opens at once; <paramref name="changes"/> fills it in when it completes.
    /// </summary>
    Task ShowWhatsNewDialogAsync(string current, string offered, Task<ChangelogResult> changes);

    /// <summary>
    /// Opens the delegated-tasks view (#67), so work another session handed to a profile stays visible and stoppable.
    /// </summary>
    Task ShowDelegatedTasksDialogAsync();

    /// <summary>
    /// Opens the test-runs view (AC-2028): the runs `start_run` began, running, queued and finished.
    /// </summary>
    Task ShowTestRunsDialogAsync();

    /// <summary>
    /// Opens the read-only window on the agent line (AC-397): what agents on this desk said, wakes asked for,
    /// what's claimed and refused. Takes its view model like the worktrees dialog does, since the caller owns it.
    /// </summary>
    Task ShowAgentLineInspectorDialogAsync(AgentLineInspectorViewModel inspector);

    /// <summary>
    /// Shows the command palette (#: command palette) over the given commands; runs the chosen one after the palette closes.
    /// </summary>
    Task ShowCommandPaletteDialogAsync(IReadOnlyList<PaletteCommand> commands);

    // The Agents palette (AC-1933): the Agents panel as a compact kanban overlay.
    Task ShowAgentsPaletteDialogAsync(AgentsOverviewViewModel agents);


    /// <summary>
    /// Shows the Set-status dialog (AC-32) seeded with <paramref name="currentStatusline"/>. Returns the new value
    /// — empty when cleared — or null when cancelled, leaving the status unchanged.
    /// </summary>
    Task<string?> ShowSetStatusDialogAsync(string currentStatusline);

    /// <summary>
    /// Shows a session's live MCP set. The operator can remove a server here; adding remains an assistant action.
    /// </summary>
    Task ShowSessionMcpDialogAsync(string paneId, string sessionName);

    /// <summary>
    /// Asks for a moment and a prompt to pick a session up with (AC-231), starting from <paramref name="suggested"/>.
    /// Null when the operator backed out.
    /// </summary>
    Task<(DateTimeOffset Moment, string Prompt)?> ShowScheduleResumeDialogAsync(DateTimeOffset suggested, string prompt);
}
