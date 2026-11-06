using System.Collections.Concurrent;
using System.Collections.Frozen;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Mcp;

namespace Cockpit.Core.Sessions;

// AC-927: where every launch route says which MCP servers a session actually got, so the header names those
// instead of the pre-launch selection, which never holds the always-mounted or auto-mounted ones it also got.
// AC-1148: and kept, not just announced — this is what every cockpit endpoint authorizes a session against.
public sealed class SessionMcpMounts : ISingletonService
{
    private readonly ConcurrentDictionary<string, SessionMcpSet> _mounted = new(StringComparer.Ordinal);

    public event Action<string>? Changed;

    public void Bind(string paneId, SessionMcpContext context)
    {
        _mounted.AddOrUpdate(paneId,
            _ => new SessionMcpSet(context, Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase)),
            (_, current) => current with { Context = current.Context?.Manager == SessionMcpManager.Assistant
                ? context with { Manager = SessionMcpManager.Assistant } : context });
        Changed?.Invoke(paneId);
    }

    public SessionMcpSet? Set(string paneId) => _mounted.GetValueOrDefault(paneId);

    public void RestrictDelegation(string paneId, IReadOnlySet<string> ceiling)
    {
        var names = ceiling.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _mounted.AddOrUpdate(paneId,
            _ => new SessionMcpSet(null, Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase)) { DelegationCeiling = names },
            (_, current) => current with { DelegationCeiling = names });
    }

    public bool TryChange(string paneId, SessionMcpSet expected, string name, bool add)
    {
        var names = expected.ServerNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (add)
        {
            names.Add(name);
        }
        else
        {
            names.Remove(name);
        }
        var context = expected.Context;
        if (!_mounted.TryUpdate(paneId, expected with { Context = context, PreserveSelection = true, ServerNames = names.ToFrozenSet(StringComparer.OrdinalIgnoreCase) }, expected))
        {
            return false;
        }
        Changed?.Invoke(paneId);
        return true;
    }

    // Raised with the pane, the servers that route mounted for it, and — AC-997 — the ones it tried and never
    // got tools from, each with a one-line reason. Once per launch: a route reports what it resolved, it does
    // not track a session afterwards.
    public event Action<string, IReadOnlyList<string>, IReadOnlyList<McpServerConnectionIssue>>? Reported;

    public void Report(string paneId, IReadOnlyList<string> connectedServerNames, IReadOnlyList<McpServerConnectionIssue>? issues = null)
    {
        Grant(paneId, connectedServerNames);
        Reported?.Invoke(paneId, connectedServerNames, issues ?? []);
    }

    public void ReportConnections(string paneId, IReadOnlyList<string> connectedServerNames, IReadOnlyList<McpServerConnectionIssue> issues, Action<Exception> onHandlerError)
    {
        foreach (var handler in Reported?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<string, IReadOnlyList<string>, IReadOnlyList<McpServerConnectionIssue>>)handler)(paneId, connectedServerNames, issues);
            }
            catch (Exception exception)
            {
                onHandlerError(exception);
            }
        }
    }

    // AC-1148: records the mount decision without announcing it — for a route that grants before it connects,
    // being its own client. Last write wins, the same "once per launch" rule Report follows, so a relaunch in
    // this pane replaces the grant rather than widening it with the previous session's.
    public void Grant(string paneId, IReadOnlyList<string> serverNames)
    {
        var names = serverNames.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _mounted.AddOrUpdate(paneId,
            _ => new SessionMcpSet(null, names),
            (_, current) => current.PreserveSelection ? current : current with
            {
                ServerNames = names,
                PreserveSelection = current.Context?.Manager == SessionMcpManager.Assistant,
            });
        Changed?.Invoke(paneId);
    }

    // AC-1148: whether this pane's launch mounted <paramref name="serverName"/>. Unknown pane reads as false —
    // no launch said so, so nothing grants it.
    public bool IsMounted(string paneId, string serverName) =>
        _mounted.TryGetValue(paneId, out var set) && set.ServerNames.Contains(serverName);
}
