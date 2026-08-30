using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.App.Services;

// Samples what the cockpit and its sessions are using (#78): one process-table read per tick serves every
// session, and each session is measured as a *tree* (the `claude` process plus everything it spawned) so the
// CPU shown is the build the agent just started, not the idle parent.
public sealed class ResourceMonitor : ISingletonService
{
    private readonly IProcessTableReader _reader;
    private readonly Func<int, double?> _pressureAvg10;
    private readonly Dictionary<int, ResourceSample> _previous = [];
    private readonly SessionProcessMembership _membership = new();
    private DateTimeOffset _sampledAt = DateTimeOffset.MinValue;

    public ResourceMonitor(IProcessTableReader reader) : this(reader, LinuxSessionCgroup.PressureAvg10)
    {
    }

    // AC-1233: allocation tests replace Linux procfs pressure reads to measure snapshot indexing deterministically.
    internal ResourceMonitor(IProcessTableReader reader, Func<int, double?> pressureAvg10)
    {
        _reader = reader;
        _pressureAvg10 = pressureAvg10;
    }

    // Reads the machine once and reports what the cockpit itself and each of `measuredSessions`
    // (with their children) is using. The first call has nothing to compare against, so it reports memory and a
    // CPU of zero — a percentage only exists between two samples.
    public ResourceUsage Sample(IReadOnlyList<SessionProcessRef> measuredSessions)
    {
        var rows = _reader.Read();
        var now = DateTimeOffset.UtcNow;
        var elapsed = _sampledAt == DateTimeOffset.MinValue ? TimeSpan.Zero : now - _sampledAt;
        var cores = Environment.ProcessorCount;

        var self = _Weigh(Environment.ProcessId, ProcessTree.Sum(rows, Environment.ProcessId), elapsed, cores);

        var sessions = new List<SessionResourceUsage>(measuredSessions.Count);
        foreach (var (paneId, title, processId) in measuredSessions)
        {
            // AC-1096: membership rather than the ppid walk, so a process the session left behind keeps counting
            // after whatever launched it died and the walk can no longer reach it.
            var processes = _membership.Measure(rows, processId);
            var measured = _Weigh(processId, processes.Usage, elapsed, cores);

            // AC-1060: read on the tick that already has the pid, since the meter that matters here is the one
            // `systemd-oomd` decides on and it lives per session cgroup. Null everywhere but Linux.
            sessions.Add(new SessionResourceUsage(
                paneId,
                title,
                measured.CpuPercent,
                measured.MemoryBytes,
                _pressureAvg10(processId),
                processes.Count,
                processes.AbandonedCount));
        }

        _sampledAt = now;

        // Only when a session has actually gone: pruning allocates, and the ordinary tick measures the same
        // sessions as the one before it. The extra entry is the cockpit's own.
        if (_previous.Count > measuredSessions.Count + 1)
        {
            _Forget(measuredSessions.Select(session => session.ProcessId).ToHashSet());
        }

        // The cockpit's own tree already contains the sessions it spawned, so the total is the cockpit's tree —
        // adding the sessions on top would count them twice. The parts break that total into things the operator can
        // name: the app itself, and the MCP tool servers it started.
        return new ResourceUsage(
            self.CpuPercent,
            self.MemoryBytes,
            sessions,
            LocalModelServers.From(rows),
            CockpitBreakdown.From(rows, Environment.ProcessId, measuredSessions.Select(session => session.ProcessId).ToHashSet()));
    }

    private (double CpuPercent, long MemoryBytes) _Weigh(int processId, ResourceSample current, TimeSpan elapsed, int cores)
    {
        var cpu = _previous.TryGetValue(processId, out var previous)
            ? CpuPercent.Between(previous, current, elapsed, cores)
            : 0;

        _previous[processId] = current;
        return (cpu, current.WorkingSetBytes);
    }

    // A session that closed must not keep its remembered members and its last sample here for the life of the app.
    private void _Forget(IReadOnlyCollection<int> measuredSessions)
    {
        _membership.Retain(measuredSessions);

        foreach (var gone in _previous.Keys
            .Where(processId => processId != Environment.ProcessId && !measuredSessions.Contains(processId))
            .ToArray())
        {
            _previous.Remove(gone);
        }
    }
}

// What the cockpit is using now, its per-session breakdown, and the local model servers beside it (#78). Servers
// are kept apart from the total since they aren't the cockpit's children and outlive its sessions, but are still
// reported since they're the heaviest thing on the machine.
public sealed record ResourceUsage(
    double CpuPercent,
    long MemoryBytes,
    IReadOnlyList<SessionResourceUsage> Sessions,
    IReadOnlyList<ModelServerUsage> ModelServers,
    CockpitParts Parts)
{
    public static readonly ResourceUsage None = new(0, 0, [], [], CockpitParts.None);
}

// One session's share, measured across everything it has spawned. `PressureAvg10` is the share of the last ten
// seconds its cgroup stalled on memory (AC-1060) — null off Linux, and null for a session with no cgroup.
// `AbandonedProcessCount` is how many of its processes no longer hang off it by parent link (AC-1096).
public sealed record SessionResourceUsage(
    string PaneId,
    string Title,
    double CpuPercent,
    long MemoryBytes,
    double? PressureAvg10 = null,
    int ProcessCount = 0,
    int AbandonedProcessCount = 0);
