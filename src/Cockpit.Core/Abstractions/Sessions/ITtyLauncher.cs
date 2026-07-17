using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Starts a TTY session: asks an <see cref="ITtySessionProvider"/> how its CLI should be launched, then does
/// the launching. Provider-neutral — it knows about pseudo consoles, environments and cleanup, and nothing
/// about which agent is running inside.
/// </summary>
public interface ITtyLauncher
{
    /// <summary>
    /// Spawns <paramref name="provider"/>'s CLI in a pseudo console sized
    /// <paramref name="columns"/>×<paramref name="rows"/>, under <paramref name="profile"/> (or the host's own
    /// configuration when null), with <paramref name="options"/> as its launch-only start defaults in that
    /// provider's vocabulary. <paramref name="workingDirectory"/> overrides the global working-directory option
    /// for this one session when non-blank.
    /// <para>
    /// The returned process owns the files the launch wrote: disposing it deletes them.
    /// </para>
    /// <para>
    /// <paramref name="paneId"/> is this session pane's id (#AC-13); when non-blank it is handed to the CLI as the
    /// <c>COCKPIT_PANE_ID</c> environment variable, so the agent can name its own session to the cockpit-session
    /// MCP server's <c>set_status</c> tool.
    /// </para>
    /// </summary>
    IConPtyProcess Launch(
        ITtySessionProvider provider,
        SessionProfile? profile,
        IReadOnlyDictionary<string, string> options,
        short columns,
        short rows,
        string? workingDirectory = null,
        SessionResume? resume = null,
        string? paneId = null);
}
