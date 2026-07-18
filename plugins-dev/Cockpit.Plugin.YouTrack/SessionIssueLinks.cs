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

    // Panes whose operator turned on "attach sent images to the issue" (AC-14). Off by default and per-pane, like
    // the link itself; cleared when the pane stops tracking an issue.
    private readonly HashSet<string> _attachImages = new(StringComparer.Ordinal);

    /// <summary>Raised (on the caller's thread — every mutation here happens on the UI thread) when a pane's link changes, so the header showing it can re-render.</summary>
    public event EventHandler<string>? Changed;

    /// <summary>
    /// Raised when a ticket is picked for a session — the act a workflow can start on (#69). Unlinking does not raise
    /// it: a flow that ran when you *stopped* tracking a ticket would be doing work about work you just put down.
    /// </summary>
    public event EventHandler<IssueLinked>? Linked;

    public LinkedIssue? For(string paneId) =>
        _byPaneId.TryGetValue(paneId, out var link) ? link : null;

    /// <summary>Whether the operator turned on attaching this pane's sent images to its issue (AC-14).</summary>
    public bool AttachesImages(string paneId) => _attachImages.Contains(paneId);

    /// <summary>Turns image-attaching on or off for a pane (AC-14), raising <see cref="Changed"/> so its header re-renders.</summary>
    public void SetAttachesImages(string paneId, bool on)
    {
        if (string.IsNullOrEmpty(paneId))
        {
            return;
        }

        var changed = on ? _attachImages.Add(paneId) : _attachImages.Remove(paneId);
        if (changed)
        {
            Changed?.Invoke(this, paneId);
        }
    }

    public void Link(string paneId, LinkedIssue link, string? workingDirectory = null)
    {
        if (string.IsNullOrEmpty(paneId))
        {
            // A host that predates PaneId hands out an empty id: there is no pane to attach to, so the link is
            // dropped rather than attached to "all of them".
            return;
        }

        _byPaneId[paneId] = link;
        Changed?.Invoke(this, paneId);
        Linked?.Invoke(this, new IssueLinked(link, workingDirectory));
    }

    public void Unlink(string paneId)
    {
        // The image-attach choice is about the issue this pane tracks; dropping the issue drops the choice too.
        _attachImages.Remove(paneId);
        if (_byPaneId.Remove(paneId))
        {
            Changed?.Invoke(this, paneId);
        }
    }
}

/// <summary>A ticket was picked for a session: which ticket, and where that session is working.</summary>
internal sealed record IssueLinked(LinkedIssue Link, string? WorkingDirectory);
