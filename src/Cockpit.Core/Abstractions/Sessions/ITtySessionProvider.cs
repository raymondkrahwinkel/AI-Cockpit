namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// A CLI that can run as the real interactive TUI in one of the cockpit's panes (#9). It answers exactly one
/// question — <em>how do I start this program?</em> — and knows nothing about pseudo consoles, panes or
/// terminals: <see cref="ITtyLauncher"/> owns all of that.
/// </summary>
/// <remarks>
/// This is deliberately far smaller than <see cref="ISessionDriver"/>, and that is the whole point. A pty has
/// no approvals, no model switching, no events and no thinking budget — it has a program, arguments, an
/// environment and a window size. Claude needs the rich driver contract, which is why hosting it as a session
/// <em>driver</em> plugin would inflate that contract until nobody else could implement it; hosting it as a TTY
/// provider costs four fields.
/// </remarks>
public interface ITtySessionProvider
{
    /// <summary>Stable id of the provider this launches (<c>claude</c>, <c>codex</c>, …).</summary>
    string ProviderId { get; }

    /// <summary>
    /// Composes the launch for one session: resolves the executable, builds its CLI arguments from
    /// <paramref name="context"/>'s options, and writes whatever session-scoped files it needs (an MCP config,
    /// a status relay), naming them in the spec so the launcher can clean them up afterwards.
    /// </summary>
    TtyLaunchSpec BuildLaunch(TtyLaunchContext context);
}
