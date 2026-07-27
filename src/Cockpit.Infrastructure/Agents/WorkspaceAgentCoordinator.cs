using System.Collections.Concurrent;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;

namespace Cockpit.Infrastructure.Agents;

/// <summary>
/// The concrete roster behind <see cref="IWorkspaceAgentCoordinator"/> (AC-391): one partition per workspace id,
/// created the first time that workspace is touched and never merged with another. A <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// of partitions, each guarded by its own lock, since MCP tool calls from several sessions can land concurrently and
/// two agents in different workspaces enrolling at the same instant must never contend on each other's state.
/// </summary>
internal sealed class WorkspaceAgentCoordinator : IWorkspaceAgentCoordinator, ISingletonService
{
    private readonly ConcurrentDictionary<string, WorkspacePartition> _partitions = new(StringComparer.Ordinal);

    public void Enroll(string workspaceId, string paneId)
    {
        var partition = _partitions.GetOrAdd(workspaceId, static _ => new WorkspacePartition());
        lock (partition.Gate)
        {
            partition.Roster.Add(paneId);
        }
    }

    public bool IsEnrolled(string workspaceId, string paneId)
    {
        if (!_partitions.TryGetValue(workspaceId, out var partition))
        {
            return false;
        }

        lock (partition.Gate)
        {
            return partition.Roster.Contains(paneId);
        }
    }

    /// <summary>
    /// One workspace's slice of agent-coordination state. Only <see cref="Roster"/> exists today; claims and wake
    /// opt-in (AC-392 and beyond) belong here too once they land, as their own empty-by-default collections next to
    /// it — this type is the reason a later ticket can add them without reshaping every caller that already
    /// partitions on workspace id.
    /// </summary>
    private sealed class WorkspacePartition
    {
        public object Gate { get; } = new();

        public HashSet<string> Roster { get; } = new(StringComparer.Ordinal);
    }
}
