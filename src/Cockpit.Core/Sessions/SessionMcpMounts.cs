using Cockpit.Core.Abstractions;

namespace Cockpit.Core.Sessions;

// AC-927: where every launch route says which MCP servers a session actually got, so the header names those
// instead of the pre-launch selection — which never holds the always-mounted, auto-mounted or project-linked
// ones, and so read as "these servers are missing" while the session had them all along.
public sealed class SessionMcpMounts : ISingletonService
{
    // Raised with the pane and the servers that route mounted for it. Once per launch: a route reports what it
    // resolved, it does not track a session afterwards.
    public event Action<string, IReadOnlyList<string>>? Reported;

    public void Report(string paneId, IReadOnlyList<string> serverNames) => Reported?.Invoke(paneId, serverNames);
}
