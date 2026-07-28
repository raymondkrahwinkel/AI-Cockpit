using System.Collections.Concurrent;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;

namespace Cockpit.Infrastructure.Agents;

/// <summary>
/// The concrete roster behind <see cref="IWorkspaceAgentCoordinator"/> (AC-391): one flat set of enrolled pane
/// ids. A <see cref="ConcurrentDictionary{TKey,TValue}"/> rather than a locked <see cref="HashSet{T}"/>, since MCP
/// tool calls from several sessions' request threads land concurrently and none of Enroll/IsEnrolled/Forget needs
/// to observe the others atomically — each call only ever touches its own key.
/// </summary>
internal sealed class WorkspaceAgentCoordinator : IWorkspaceAgentCoordinator, ISingletonService
{
    private readonly ConcurrentDictionary<string, byte> _roster = new(StringComparer.Ordinal);

    public void Enroll(string paneId) => _roster[paneId] = 0;

    public bool IsEnrolled(string paneId) => _roster.ContainsKey(paneId);

    public void Forget(string paneId) => _roster.TryRemove(paneId, out _);
}
