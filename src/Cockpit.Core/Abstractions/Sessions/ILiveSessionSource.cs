namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// A place the cockpit runs sessions that the worktree guards have to know about (AC-106). The cockpit's own panes are
/// one such place and the delegation engine is another: a delegated task runs headless, with no pane, so nothing on
/// the view model ever knew it was there. Every source is folded into <see cref="ILiveSessionRegistry.LiveSessionIds"/>,
/// so the managed-worktrees panel and the agent's <c>worktree_remove</c> keep reading one answer rather than each
/// knowing about a different half of what is running.
/// </summary>
public interface ILiveSessionSource
{
    /// <summary>The ids — the pane ids worktrees are keyed on — of the sessions this source is running right now.</summary>
    IReadOnlySet<string> LiveSessionIds { get; }
}
