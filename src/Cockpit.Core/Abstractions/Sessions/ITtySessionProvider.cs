namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// A CLI that can run as the real interactive TUI in one of the cockpit's panes (#9). Answers one question —
/// <em>how do I start this program?</em> — knowing nothing of consoles or terminals (<see cref="ITtyLauncher"/>
/// owns that). Smaller than <see cref="ISessionDriver"/>: hosting it as a <em>driver</em> would inflate that rich contract; a TTY provider costs four fields.
/// </summary>
public interface ITtySessionProvider
{
    /// <summary>
    /// Stable id of the provider this launches (<c>claude</c>, <c>codex</c>, …).
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Composes the launch for one session: resolves the executable, builds its CLI arguments from
    /// <paramref name="context"/>'s options, and writes whatever session-scoped files it needs (an MCP config,
    /// a status relay), naming them in the spec so the launcher can clean them up afterwards.
    /// </summary>
    TtyLaunchSpec BuildLaunch(TtyLaunchContext context);
}
