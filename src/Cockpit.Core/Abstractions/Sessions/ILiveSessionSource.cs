namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// A place the cockpit runs sessions that the worktree guards must know about (AC-106) — panes and the headless
/// delegation engine alike. Every source folds into <see cref="ILiveSessionRegistry.LiveSessionIds"/>, so the
/// managed-worktrees panel and <c>worktree_remove</c> read one answer instead of each knowing half of it.
/// </summary>
public interface ILiveSessionSource
{
    /// <summary>The ids — the pane ids worktrees are keyed on — of the sessions this source is running right now.</summary>
    IReadOnlySet<string> LiveSessionIds { get; }
}
