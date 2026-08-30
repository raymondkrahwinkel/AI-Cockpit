namespace Cockpit.Core.Diagnostics;

// AC-1096: which processes are still a session's, once the parent chain can no longer say. A process seen in the
// session's tree stays a member until it exits, so reparenting cannot hide it: a build server whose launcher died
// keeps counting instead of dropping out of the meter at exactly the moment it becomes worth reporting.
public sealed class SessionProcessMembership
{
    private readonly Dictionary<int, HashSet<int>> _membersByRoot = [];

    // Everything still alive that this session has ever spawned, plus whatever those have spawned since. One
    // sample can only miss a process that both started and exited between two reads, which held nothing for long.
    public SessionProcesses Measure(IReadOnlyList<ProcessRow> rows, int rootProcessId)
    {
        var snapshot = ProcessTree.Snapshot(rows);
        var seeds = _membersByRoot.GetValueOrDefault(rootProcessId) ?? [];
        seeds.Add(rootProcessId);

        var members = snapshot.LiveReachableFrom(seeds);
        _membersByRoot[rootProcessId] = members;

        return new SessionProcesses(
            snapshot.SumOf(members),
            members.Count,
            snapshot.AbandonedCount(members, rootProcessId));
    }

    // AC-1086: adds every measured session's processes to `target`, so a cockpit-wide total can be taken over the
    // union of its own tree and these — a set, because the tree already holds the members still attached to it.
    public void UnionInto(HashSet<int> target)
    {
        foreach (var members in _membersByRoot.Values)
        {
            target.UnionWith(members);
        }
    }

    // Keeps only the sessions the cockpit still measures, so a closed one does not hold its remembered pids for
    // the life of the app.
    public void Retain(IReadOnlyCollection<int> rootProcessIds)
    {
        foreach (var gone in _membersByRoot.Keys.Where(root => !rootProcessIds.Contains(root)).ToArray())
        {
            _membersByRoot.Remove(gone);
        }
    }
}
