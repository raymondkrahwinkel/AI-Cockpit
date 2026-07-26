using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// Which issue each session pane is working on (#75). One instance is shared by the plugin's contributions, so
/// starting an issue from the dialog reaches the header of the session it was started for: the dialog knows the
/// active pane (<c>ICockpitSessionObserver.ActivePaneId</c>), the header knows its own
/// (<c>IPluginSessionContext.PaneId</c>), and this is the only thing that connects them.
/// <para>
/// It is also where the session gets labelled after the ticket (#AC-310). Linking used to be invisible outside this
/// plugin's own header — a session could carry a ticket while its sidebar row still read "default - 3", which is
/// exactly the session you want to pick out of four. Doing it here rather than at each call site is what makes it
/// hold for every route in: the dialog's Link to session, the session header's own picker, and the new session
/// started from an issue.
/// </para>
/// <para>
/// Deliberately not persisted: a pane's id lives as long as the pane, and the cockpit does not restore sessions
/// on restart — persisting a link to a session that will never come back is worse than asking for it again.
/// </para>
/// </summary>
internal sealed class SessionIssueLinks(ICockpitHost host)
{
    private readonly Dictionary<string, LinkedIssue> _byPaneId = new(StringComparer.Ordinal);

    /// <summary>Raised (on the caller's thread — every mutation here happens on the UI thread) when a pane's link changes, so the header showing it can re-render.</summary>
    public event EventHandler<string>? Changed;

    /// <summary>
    /// Raised when a ticket is picked for a session — the act a workflow can start on (#69). Unlinking does not raise
    /// it: a flow that ran when you *stopped* tracking a ticket would be doing work about work you just put down.
    /// </summary>
    public event EventHandler<IssueLinked>? Linked;

    public LinkedIssue? For(string paneId) =>
        _byPaneId.TryGetValue(paneId, out var link) ? link : null;

    public void Link(string paneId, LinkedIssue link, string? workingDirectory = null)
    {
        if (string.IsNullOrEmpty(paneId))
        {
            // A host that predates PaneId hands out an empty id: there is no pane to attach to, so the link is
            // dropped rather than attached to "all of them".
            return;
        }

        _byPaneId[paneId] = link;

        // The statusline follows the link unconditionally — saying what a session is working on is what it is for.
        // The name is only suggested, because a session the operator named themselves has a name that means
        // something to them, and a ticket id is not worth losing it over (#AC-310).
        _ = host.SetSessionStatusline(paneId, $"{link.Issue.IdReadable} — {link.Issue.Summary}");
        _ = host.SuggestSessionName(paneId, link.Issue.IdReadable);

        Changed?.Invoke(this, paneId);
        Linked?.Invoke(this, new IssueLinked(link, workingDirectory));
    }

    public void Unlink(string paneId)
    {
        if (_byPaneId.Remove(paneId))
        {
            // The label goes with the link: a statusline still naming a ticket you just put down is worse than none.
            // The name stays as it is — the one it had before the link is not ours to restore.
            _ = host.SetSessionStatusline(paneId, string.Empty);
            Changed?.Invoke(this, paneId);
        }
    }
}

/// <summary>A ticket was picked for a session: which ticket, and where that session is working.</summary>
internal sealed record IssueLinked(LinkedIssue Link, string? WorkingDirectory);
