namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// The values a plugin can pre-fill the cockpit's New-session dialog with (#AC-96) when it opens it through
/// <see cref="ICockpitHost.ShowNewSessionDialogAsync"/> — so an assistant that knows which profile and folder a
/// task wants can offer them ready, while the operator still confirms, edits, or overrides every field before the
/// session starts. Every field is optional: a <see langword="null"/> or blank one leaves that part of the dialog on
/// its own default, so a plugin fills only what it actually knows.
/// <para>
/// A prefill only seeds the dialog; it does not start anything. Nothing is created until the operator presses Start,
/// and what starts is what the dialog then shows — the operator's final choices, not the plugin's suggestion. This
/// is deliberately the whole dialog, not a headless launch: the plugin never gets to pick a profile, a working tree
/// or a resume target on the operator's behalf without the operator seeing it (that quieter path is
/// <see cref="ICockpitActions.StartSessionAsync"/>, which names a profile the operator already trusts).
/// </para>
/// </summary>
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
    /// The project to open the dialog on, named by the link it carries rather than by id (#AC-419): a plugin says "the
    /// project tracked in YouTrack's <c>AC</c>" and the host preselects the one that declares it — so a session started
    /// from an issue no longer opens on <em>No project</em> on a dialog that already knows which issue it is for.
    /// <para>
    /// Nothing is invented and nothing is guessed. A link no project declares leaves the picker exactly where it was,
    /// and so does one that two projects declare: an unselected field the operator still reads beats a confident wrong
    /// one they stop reading. Preselecting is all it does — the project's own folder, profile and worktree defaults
    /// land first and every one of them, the project included, stays editable until Start, and an explicit
    /// <see cref="WorkingDirectory"/> here is applied over the project's.
    /// </para>
    /// <para>
    /// An <c>init</c> property rather than a sixth parameter on the primary constructor, because this record is
    /// constructed by plugin binaries that are already published: adding the parameter is source-compatible but
    /// binary-breaking, and an installed plugin would fail to bind the constructor it compiled against. Same reasoning
    /// as <see cref="ICockpitHost.AddMcpEndpoint(string, object, System.Func{bool}?)"/> keeping its original signature
    /// beside the wider one. A plugin that sets this needs a host that reads it, so it sets <c>minHostVersion</c> 0.8.0
    /// — on an older host the setter is simply not there.
    /// </para>
    /// </summary>
    public ProjectLink? LinkedProject { get; init; }
}
