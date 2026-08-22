namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// The ids of the sessions alive right now, as worktree teardown/removal see them (AC-85): removing a worktree
/// whose owning id is in <see cref="LiveSessionIds"/> would pull the working directory out from under it. Built
/// from the cockpit's panes plus every <see cref="ILiveSessionSource"/> without one, e.g. delegation (AC-106).
/// </summary>
public interface ILiveSessionRegistry
{
    /// <summary>The session ids (the pane ids worktrees are keyed on) of the sessions running right now.</summary>
    IReadOnlySet<string> LiveSessionIds { get; }
}
