namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// Which issue each session pane is working on (#75). One instance is shared by the plugin's contributions, so
/// starting an issue from the dialog reaches the header of the session it was started for: the dialog knows the
/// active pane (<c>ICockpitSessionObserver.ActivePaneId</c>), the header knows its own
/// (<c>IPluginSessionContext.PaneId</c>), and this is the only thing that connects them.
/// <para>
/// Deliberately not persisted: a pane's id lives as long as the pane, and the cockpit does not restore sessions
/// on restart — persisting a link to a session that will never come back is worse than asking for it again.
/// </para>
/// </summary>
internal sealed class SessionIssueLinks
{
    private readonly Dictionary<string, LinkedIssue> _byPaneId = new(StringComparer.Ordinal);

    /// <summary>Raised (on the caller's thread — every mutation here happens on the UI thread) when a pane's link changes, so the header showing it can re-render.</summary>
    public event EventHandler<string>? Changed;

    public LinkedIssue? For(string paneId) =>
        _byPaneId.TryGetValue(paneId, out var link) ? link : null;

    public void Link(string paneId, LinkedIssue link)
    {
        if (string.IsNullOrEmpty(paneId))
        {
            // A host that predates PaneId hands out an empty id: there is no pane to attach to, so the link is
            // dropped rather than attached to "all of them".
            return;
        }

        _byPaneId[paneId] = link;
        Changed?.Invoke(this, paneId);
    }

    public void Unlink(string paneId)
    {
        if (_byPaneId.Remove(paneId))
        {
            Changed?.Invoke(this, paneId);
        }
    }
}
