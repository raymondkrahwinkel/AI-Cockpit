using System.Collections.Concurrent;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;

namespace Cockpit.Infrastructure.Agents;

/// <summary>
/// The concrete roster behind <see cref="IWorkspaceAgentCoordinator"/> (AC-391): one flat set of enrolled pane
/// ids, each carrying whether that pane has agreed to be woken (AC-395). A
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> rather than a locked <see cref="HashSet{T}"/>, since MCP
/// tool calls from several sessions' request threads land concurrently and none of these calls needs to observe
/// the others atomically — each only ever touches its own key.
/// </summary>
internal sealed class WorkspaceAgentCoordinator : IWorkspaceAgentCoordinator, ISingletonService
{
    // Key present = enrolled; value = wake consent. One entry rather than two dictionaries, so a pane cannot end
    // up enrolled in one and remembered in the other.
    private readonly ConcurrentDictionary<string, bool> _roster = new(StringComparer.Ordinal);

    // TryAdd, not an indexer assignment. Enroll runs on every cockpit-agents call an agent makes, and the value
    // now carries consent — so assigning would quietly revoke the opt-in on the pane's very next list_agents or
    // notify, leaving an agent that had said yes never woken and nothing anywhere saying why.
    public void Enroll(string paneId) => _roster.TryAdd(paneId, false);

    public bool IsEnrolled(string paneId) => _roster.ContainsKey(paneId);

    public void SetWakeConsent(string paneId, bool consents) => _roster[paneId] = consents;

    public bool HasWakeConsent(string paneId) => _roster.TryGetValue(paneId, out var consents) && consents;

    public void Forget(string paneId) => _roster.TryRemove(paneId, out _);
}
