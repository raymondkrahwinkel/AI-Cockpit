namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// The values a plugin can pre-fill the cockpit's New-session dialog with (#AC-96) when it opens it through
/// <see cref="ICockpitHost.ShowNewSessionDialogAsync"/>. Every field is optional: a <see langword="null"/> or
/// blank one leaves that part of the dialog on its own default.
/// </summary>
/// <remarks>
/// A prefill only seeds the dialog — nothing starts until the operator presses Start, and what starts is the
/// operator's final choices in the dialog, not the plugin's suggestion. For a quieter path that starts a session
/// directly on a profile the operator already trusts, without showing the dialog, see
/// <see cref="ICockpitActions.StartSessionAsync"/>.
/// </remarks>
/// <param name="ProfileLabel">
/// The session profile to preselect, matched by its label (case-insensitively) against the configured profiles; a
/// label that matches none leaves the dialog's own default selection. Deliberately a label rather than a profile
/// object — a plugin sees profiles as names (see <c>PluginProfileInfo</c>), never the host's <c>SessionProfile</c>.
/// </param>
/// <param name="WorkingDirectory">
/// The folder to pre-fill as the session's working directory; blank leaves it to the operator.
/// </param>
/// <param name="SessionName">
/// The friendly session name to pre-fill (shown in the sidebar and header); blank falls back to the dialog's generated name.
/// </param>
/// <param name="InitialPrompt">
/// A first prompt to place into the started session's input once it exists — injected through the same seam a
/// plugin's <c>ICockpitActions.InjectIntoActiveSessionAsync</c> uses, so the operator sees it in the composer and
/// still decides when (or whether) to send it. Blank injects nothing.
/// </param>
/// <param name="ResumeSessionId">
/// The id of an earlier conversation to resume: sets the dialog to resume-by-id and fills the id, so the operator
/// can start where a previous session left off. Blank starts a fresh conversation. Only providers that keep a
/// resumable history (the Claude CLI) act on it; the dialog hides the resume controls for the rest.
/// </param>
public sealed record NewSessionPrefill(
    string? ProfileLabel = null,
    string? WorkingDirectory = null,
    string? SessionName = null,
    string? InitialPrompt = null,
    string? ResumeSessionId = null)
{
    /// <summary>
    /// The project to open the dialog on, named by the link it carries rather than by id (#AC-419): a plugin says
    /// "the project tracked in YouTrack's <c>AC</c>" and the host preselects the one that declares it. Nothing is
    /// invented or guessed — a link no project declares, or that two projects declare, leaves the picker exactly
    /// where it was.
    /// </summary>
    /// <remarks>
    /// Preselecting is all it does: the project's own folder, profile and worktree defaults land first and every
    /// one of them, the project included, stays editable until Start, with an explicit
    /// <see cref="WorkingDirectory"/> here applied over the project's. A plugin that sets this needs a host that
    /// reads it, so it sets <c>minHostVersion</c> 0.8.0 — on an older host the setter is simply not there.
    /// </remarks>
    public ProjectLink? LinkedProject { get; init; }
}
