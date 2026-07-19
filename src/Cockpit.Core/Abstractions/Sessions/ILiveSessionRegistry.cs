namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// The ids of the sessions alive right now, as the worktree teardown and removal paths see them (AC-85): a worktree
/// whose owning session id is in <see cref="LiveSessionIds"/> is still running, so removing it would pull the working
/// directory out from under that session. The cockpit implements this from the panes it shows; a headless run (a
/// delegated agent's MCP server with no live UI) reports none, and the startup reconcile is the net there instead.
/// </summary>
public interface ILiveSessionRegistry
{
    /// <summary>The session ids (the pane ids worktrees are keyed on) of the sessions running right now.</summary>
    IReadOnlySet<string> LiveSessionIds { get; }
}
