using System.Collections.Concurrent;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Mcp;

namespace Cockpit.Core.Sessions;

// AC-927: where every launch route says which MCP servers a session actually got, so the header names those
// instead of the pre-launch selection, which never holds the always-mounted or auto-mounted ones it also got.
// AC-1148: and kept, not just announced — this is what every cockpit endpoint authorizes a session against.
public sealed class SessionMcpMounts : ISingletonService
{
    private readonly ConcurrentDictionary<string, IReadOnlySet<string>> _mounted = new(StringComparer.Ordinal);

    // Raised with the pane, the servers that route mounted for it, and — AC-997 — the ones it tried and never
    // got tools from, each with a one-line reason. Once per launch: a route reports what it resolved, it does
    // not track a session afterwards.
    public event Action<string, IReadOnlyList<string>, IReadOnlyList<McpServerConnectionIssue>>? Reported;

    public void Report(string paneId, IReadOnlyList<string> connectedServerNames, IReadOnlyList<McpServerConnectionIssue>? issues = null)
    {
        Grant(paneId, connectedServerNames);
        Reported?.Invoke(paneId, connectedServerNames, issues ?? []);
    }

    // AC-1148: records the mount decision without announcing it — for a route that grants before it connects,
    // being its own client. Last write wins, the same "once per launch" rule Report follows, so a relaunch in
    // this pane replaces the grant rather than widening it with the previous session's.
    public void Grant(string paneId, IReadOnlyList<string> serverNames) =>
        _mounted[paneId] = new HashSet<string>(serverNames, StringComparer.OrdinalIgnoreCase);

    // AC-1148: whether this pane's launch mounted <paramref name="serverName"/>. Unknown pane reads as false —
    // no launch said so, so nothing grants it.
    public bool IsMounted(string paneId, string serverName) =>
        _mounted.TryGetValue(paneId, out var names) && names.Contains(serverName);
}
