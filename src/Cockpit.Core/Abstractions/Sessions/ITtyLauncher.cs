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
    /// Spawns <paramref name="provider"/>'s CLI in a sized pseudo console under <paramref name="profile"/>; owns
    /// and deletes its launch files on dispose. Carries <paramref name="paneId"/> as <c>COCKPIT_PANE_ID</c>
    /// (#AC-13) for self-labelling, an MCP narrowing (#44), plugin <paramref name="contributed"/> (AC-165), and <paramref name="projectId"/> (AC-218).
    /// </summary>
    IConPtyProcess Launch(
        ITtySessionProvider provider,
        SessionProfile? profile,
        IReadOnlyDictionary<string, string> options,
        short columns,
        short rows,
        string? workingDirectory = null,
        SessionResume? resume = null,
        string? paneId = null,
        IReadOnlySet<string>? enabledMcpServerNames = null,
        SessionResources? contributed = null,
        string? projectId = null);
}
