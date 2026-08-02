using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubIssues;

// Which GitHub issue each session is working on (#77) — by pane, not by "the active session", which is a guess the
// moment four panes are open. The same arrangement the YouTrack plugin uses, and for the same reason.
//
// It is also where the session gets labelled after the issue (#AC-310). Linking used to be invisible outside this
// plugin's own header — a session could carry an issue while its sidebar row still read "default - 3", which is
// exactly the session you want to pick out of four. Doing it here rather than at each call site is what makes it
// hold for every route in: the dialog's Link to session, the session header's own picker, and the new session
// started from an issue.
//
// Deliberately not persisted: the cockpit does not restore sessions on startup, so a link kept for a pane that never
// comes back is worse than asking again.
internal sealed class SessionIssueLinks(ICockpitHost host)
{
    private readonly Dictionary<string, GitHubIssue> _byPaneId = new(StringComparer.Ordinal);

    // Raised when a pane's link changes, so the header showing it can re-render.
    public event EventHandler<string>? Changed;

    // Raised when an issue is picked for a session — the act a workflow can start on. Unlinking does not raise it: a flow that ran when you *stopped* working on something would be doing work about work you just put down.
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

        // The statusline follows the link unconditionally — saying what a session is working on is what it is for.
        // The name is only suggested, because a session the operator named themselves has a name that means
        // something to them, and an issue number is not worth losing it over (#AC-310).
        _ = host.SetSessionStatusline(paneId, SessionLabel.Statusline(issue));
        _ = host.SuggestSessionName(paneId, SessionLabel.Name(issue));

        Changed?.Invoke(this, paneId);
        Picked?.Invoke(this, new IssuePicked(issue, workingDirectory));
    }

    public void Unlink(string paneId)
    {
        if (_byPaneId.Remove(paneId))
        {
            // The label deliberately stays. Clearing it looks tidier until you notice the statusline is shared: the
            // agent sets its own progress there through the session-status tool, and a flow through cockpit.set-status.
            // The host offers no way to read the line back, so "clear it if it is still mine" is not a thing this can
            // know — and wiping someone's live progress is a worse trade than leaving an issue title a moment too long.
            Changed?.Invoke(this, paneId);
        }
    }
}

// An issue was picked for a session: which issue, and where that session is working.
internal sealed record IssuePicked(GitHubIssue Issue, string? WorkingDirectory);
