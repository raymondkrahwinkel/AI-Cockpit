namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// Optional interface a plugin's settings view (the control passed to <see cref="ICockpitHost.AddSettings"/>)
/// can implement so the host's settings screen provides a standard Save/Close footer for it (#14). A settings
/// view that applies changes live and needs no explicit save can skip this — it just gets a Close button.
/// <para>
/// <strong>The view validates and hands over; the host writes.</strong> Nothing is persisted while the operator
/// is still in the screen, because the screen the settings sit in may be cancelled: the cockpit's Options
/// dialog is one staged transaction (AC-999) where Cancel has to take a plugin's change back too, and it cannot
/// take back a write that already happened. So <see cref="TryStage"/> only checks the fields and answers with
/// the write to perform; the host decides whether and when to run it, and reverting is simply never running it.
/// </para>
/// <para>
/// The same contract serves both hosts. The standalone settings window (a plugin's own gear, a widget pane's
/// gear) stages and commits in one click; the Options screen holds the commit until the operator applies. A view
/// notices no difference.
/// </para>
/// <para>
/// <strong>Migrating from <c>bool Save()</c>:</strong> move the body into the <c>commit</c> you hand back, and
/// return the reason you used to show yourself as <c>error</c>. See <c>docs/plugins/PLUGIN-SDK.md</c>.
/// </para>
/// </summary>
public interface IPluginSettingsView
{
    /// <summary>
    /// Validates the current field values <em>without writing anything</em>, and hands the host the write to
    /// perform on success.
    /// </summary>
    /// <param name="commit">
    /// Runs the plugin's own persistence — storage writes and whatever else saving means for it (registering an
    /// MCP server, dropping an orphaned entry). Called by the host at most once, after every other staged change
    /// has been accepted, and never at all when the operator cancels. Read the view's fields as it runs or
    /// capture them while staging, as you prefer: the host stages at the moment the operator confirms, so
    /// nothing moves in between.
    /// </param>
    /// <param name="error">
    /// One line the operator can act on, shown by the host when validation fails — "Two connections are named
    /// 'work'", not "invalid input". The view may also mark the offending field itself; this is what the host
    /// has to say when it cannot.
    /// </param>
    /// <returns><see langword="true"/> when the values are good and <paramref name="commit"/> is set; <see langword="false"/> when they are not and <paramref name="error"/> says why.</returns>
    /// <remarks>
    /// Deliberately without <c>[NotNullWhen]</c> annotations: they would make every implementation that does not
    /// repeat them warn (CS8767), which is a tax on a member a dozen plugins implement — and the host has to
    /// handle a plugin that breaks the contract anyway, annotations or not.
    /// </remarks>
    bool TryStage(out Action? commit, out string? error);
}
