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

    // Reads the machine once and reports what the cockpit itself and each of `sessionProcessIds`
    // (with their children) is using. The first call has nothing to compare against, so it reports memory and a
    // CPU of zero — a percentage only exists between two samples.
    public ResourceUsage Sample(IReadOnlyDictionary<string, int> sessionProcessIds)
    {
        var rows = _reader.Read();
        var now = DateTimeOffset.UtcNow;
        var elapsed = _sampledAt == DateTimeOffset.MinValue ? TimeSpan.Zero : now - _sampledAt;
        var cores = Environment.ProcessorCount;

        var self = _Measure(Environment.ProcessId, rows, elapsed, cores);

        var sessions = new List<SessionResourceUsage>();
        foreach (var (title, processId) in sessionProcessIds)
        {
            var measured = _Measure(processId, rows, elapsed, cores);

            // AC-1060: read on the tick that already has the pid, since the meter that matters here is the one
            // `systemd-oomd` decides on and it lives per session cgroup. Null everywhere but Linux.
            sessions.Add(new SessionResourceUsage(
                title,
                measured.CpuPercent,
                measured.MemoryBytes,
                _pressureAvg10(processId)));
        }

        _sampledAt = now;

        // The cockpit's own tree already contains the sessions it spawned, so the total is the cockpit's tree —
        // adding the sessions on top would count them twice. The parts break that total into things the operator can
        // name: the app itself, and the MCP tool servers it started.
        return new ResourceUsage(
            self.CpuPercent,
            self.MemoryBytes,
            sessions,
            LocalModelServers.From(rows),
            CockpitBreakdown.From(rows, Environment.ProcessId, sessionProcessIds.Values.ToHashSet()));
    }

    private (double CpuPercent, long MemoryBytes) _Measure(int processId, IReadOnlyList<ProcessRow> rows, TimeSpan elapsed, int cores)
    {
        var current = ProcessTree.Sum(rows, processId);
        var cpu = _previous.TryGetValue(processId, out var previous)
            ? CpuPercent.Between(previous, current, elapsed, cores)
            : 0;

        _previous[processId] = current;
        return (cpu, current.WorkingSetBytes);
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

// One session's share, measured across its whole process tree. `PressureAvg10` is the share of the last ten
// seconds its cgroup stalled on memory (AC-1060) — null off Linux, and null for a session with no cgroup.
public sealed record SessionResourceUsage(string Title, double CpuPercent, long MemoryBytes, double? PressureAvg10 = null);
