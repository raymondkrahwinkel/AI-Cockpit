namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// Which GitHub issue each session is working on (#77) — by pane, not by "the active session", which is a guess the
/// moment four panes are open. The same arrangement the YouTrack plugin uses, and for the same reason.
/// <para>
/// Deliberately not persisted: the cockpit does not restore sessions on startup, so a link kept for a pane that never
/// comes back is worse than asking again.
/// </para>
/// </summary>
internal sealed class SessionIssueLinks
{
    private readonly Dictionary<string, GitHubIssue> _byPaneId = new(StringComparer.Ordinal);

    /// <summary>Raised when a pane's link changes, so the header showing it can re-render.</summary>
    public event EventHandler<string>? Changed;

    /// <summary>Raised when an issue is picked for a session — the act a workflow can start on. Unlinking does not raise it: a flow that ran when you *stopped* working on something would be doing work about work you just put down.</summary>
    public event EventHandler<IssuePicked>? Picked;

    public GitHubIssue? For(string paneId) =>
        _byPaneId.TryGetValue(paneId, out var issue) ? issue : null;

    public void Link(string paneId, GitHubIssue issue, string? workingDirectory = null)
    {
        if (string.IsNullOrEmpty(paneId))
        {
            // A host that predates PaneId hands out an empty id: there is no pane to attach to, so the link is
            // dropped rather than attached to all of them.
            return;
        }

        _byPaneId[paneId] = issue;
        Changed?.Invoke(this, paneId);
        Picked?.Invoke(this, new IssuePicked(issue, workingDirectory));
    }

    public void Unlink(string paneId)
    {
        if (_byPaneId.Remove(paneId))
        {
            Changed?.Invoke(this, paneId);
        }
    }
}

/// <summary>An issue was picked for a session: which issue, and where that session is working.</summary>
internal sealed record IssuePicked(GitHubIssue Issue, string? WorkingDirectory);
