using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;

namespace Cockpit.App.Services;

/// <summary>
/// Samples what the cockpit and its sessions are using (#78), on a timer, and hands back both the total and the
/// per-session breakdown. One read of the process table per tick serves every session on screen — walking it once
/// per session would re-read the whole table for each pane.
/// <para>
/// A session is measured as a <em>tree</em>: the <c>claude</c> process plus everything it spawned. That is the
/// whole point — the CPU an operator wants to see is the build the agent just started, not the idle parent.
/// </para>
/// </summary>
public sealed class ResourceMonitor(IProcessTableReader reader) : ISingletonService
{
    private readonly Dictionary<int, ResourceSample> _previous = [];
    private DateTimeOffset _sampledAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Reads the machine once and reports what the cockpit itself and each of <paramref name="sessionProcessIds"/>
    /// (with their children) is using. The first call has nothing to compare against, so it reports memory and a
    /// CPU of zero — a percentage only exists between two samples.
    /// </summary>
    public ResourceUsage Sample(IReadOnlyDictionary<string, int> sessionProcessIds)
    {
        var rows = reader.Read();
        var now = DateTimeOffset.UtcNow;
        var elapsed = _sampledAt == DateTimeOffset.MinValue ? TimeSpan.Zero : now - _sampledAt;
        var cores = Environment.ProcessorCount;

        var self = _Measure(Environment.ProcessId, rows, elapsed, cores);

        var sessions = new List<SessionResourceUsage>();
        foreach (var (title, processId) in sessionProcessIds)
        {
            var measured = _Measure(processId, rows, elapsed, cores);
            sessions.Add(new SessionResourceUsage(title, measured.CpuPercent, measured.MemoryBytes));
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

/// <summary>
/// What the cockpit is using right now, how that breaks down per session, and what the local model servers beside it
/// are holding (#78). The servers are apart from the total on purpose: they are not the cockpit's children and they
/// outlive its sessions, so folding them into "what this app costs" would be wrong — but leaving them out of the
/// panel entirely meant the app had nothing to say about the heaviest thing on the machine.
/// </summary>
public sealed record ResourceUsage(
    double CpuPercent,
    long MemoryBytes,
    IReadOnlyList<SessionResourceUsage> Sessions,
    IReadOnlyList<ModelServerUsage> ModelServers,
    CockpitParts Parts)
{
    public static readonly ResourceUsage None = new(0, 0, [], [], CockpitParts.None);
}

/// <summary>One session's share, measured across its whole process tree.</summary>
public sealed record SessionResourceUsage(string Title, double CpuPercent, long MemoryBytes);
