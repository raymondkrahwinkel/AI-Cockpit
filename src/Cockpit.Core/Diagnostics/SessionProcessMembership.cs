namespace Cockpit.Core.Diagnostics;

// AC-1096: which processes are still a session's, once the parent chain can no longer say. A process seen in the
// session's tree stays a member until it exits, so reparenting cannot hide it: a build server whose launcher died
// keeps counting instead of dropping out of the meter at exactly the moment it becomes worth reporting.
public sealed class SessionProcessMembership
{
    // AC-1331: a shell alive in a session's first samples is how its MCP servers were launched (`cmd /c npx` on
    // Windows), not work — it is remembered so `OutstandingCount` can look past it for the life of the session.
    internal const int LauncherSamples = 2;

    private readonly Dictionary<int, HashSet<int>> _membersByRoot = [];
    private readonly Dictionary<int, HashSet<int>> _launcherShellsByRoot = [];
    private readonly Dictionary<int, int> _samplesByRoot = [];

    // Everything still alive that this session has ever spawned, plus whatever those have spawned since. One
    // sample can only miss a process that both started and exited between two reads, which held nothing for long.
    public SessionProcesses Measure(IReadOnlyList<ProcessRow> rows, int rootProcessId, bool rootIsShell = false)
    {
        var snapshot = ProcessTree.Snapshot(rows);
        var seeds = _membersByRoot.GetValueOrDefault(rootProcessId) ?? [];
        seeds.Add(rootProcessId);

        var members = snapshot.LiveReachableFrom(seeds);
        _membersByRoot[rootProcessId] = members;

        var samples = _samplesByRoot.GetValueOrDefault(rootProcessId) + 1;
        _samplesByRoot[rootProcessId] = samples;
        var launcherShells = _launcherShellsByRoot.GetValueOrDefault(rootProcessId) ?? [];
        if (samples <= LauncherSamples)
        {
            snapshot.AddShellsBelow(members, rootProcessId, launcherShells);
            _launcherShellsByRoot[rootProcessId] = launcherShells;
        }

        // AC-1310: the root drops out of `members` once it exits, so subtract it only while it is still there.
        return new SessionProcesses(
            snapshot.SumOf(members),
            members.Count,
            members.Count - (members.Contains(rootProcessId) ? 1 : 0),
            snapshot.AbandonedCount(members, rootProcessId),
            snapshot.OutstandingCount(members, launcherShells, rootProcessId, rootIsShell));
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
            _launcherShellsByRoot.Remove(gone);
            _samplesByRoot.Remove(gone);
        }
    }
}
