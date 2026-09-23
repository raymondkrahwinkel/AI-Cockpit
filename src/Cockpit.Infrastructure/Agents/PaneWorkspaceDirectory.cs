using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Agents;

// Host-side `IPaneWorkspaceDirectory` (AC-439): every live agent pane by its desk. AC-1373: read from the session
// registry's snapshot, which is what AC-1201's off-thread caller needs, so the hop onto the UI thread is gone.
internal sealed class PaneWorkspaceDirectory(ISessionRegistry sessions) : IPaneWorkspaceDirectory, ISingletonService
{
    public IReadOnlyDictionary<string, string> WorkspaceIdsByPane()
    {
        var byPane = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var session in sessions.All.Where(session => !session.IsTerminal))
        {
            // A pane placed nowhere is left out of the directory entirely, which is what collision detection
            // wants: the assistant shares no desk with anything, so it can collide with nothing.
            if (session.PlacedWorkspaceId is { } workspaceId)
            {
                byPane[session.PaneId] = workspaceId;
            }
        }

        return byPane;
    }
}
