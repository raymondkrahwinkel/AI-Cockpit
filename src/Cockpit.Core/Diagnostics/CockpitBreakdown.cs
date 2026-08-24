namespace Cockpit.Core.Diagnostics;

// The status bar's total includes MCP tool servers (`npm exec`, `uv`) the cockpit spawns that are neither a
// session nor a model server, so this breaks them out explicitly rather than leaving them unexplained (#78).
// Each child is measured as a tree; sessions are excluded here since they already have their own section.
public static class CockpitBreakdown
{
    // The cockpit's own process, and each of its children that is not a session, heaviest first. Together with the
    // sessions these are the total — which is what makes the total explicable.
    public static CockpitParts From(IReadOnlyList<ProcessRow> rows, int cockpitProcessId, IReadOnlyCollection<int> sessionProcessIds)
    {
        var own = rows.FirstOrDefault(row => row.ProcessId == cockpitProcessId)?.WorkingSetBytes ?? 0;

        var children = rows
            .Where(row => row.ParentProcessId == cockpitProcessId && !sessionProcessIds.Contains(row.ProcessId))
            .Select(row => new ProcessGroupUsage(row.Name, ProcessTree.Sum(rows, row.ProcessId).WorkingSetBytes, row.ProcessId))
            // Two MCP servers started the same way carry the same name; they are one line, because "npm exec" twice
            // over is not a thing the operator can tell apart or act on separately.
            .GroupBy(child => child.Name, StringComparer.Ordinal)
            .Select(group => new ProcessGroupUsage(
                group.Count() == 1 ? group.Key : $"{group.Key} ×{group.Count()}",
                group.Sum(child => child.MemoryBytes),
                // AC-734: null once merged — two same-named processes have no single id left to mean.
                group.Count() == 1 ? group.Single().ProcessId : null))
            .OrderByDescending(child => child.MemoryBytes)
            .ToList();

        return new CockpitParts(own, children);
    }
}

// The cockpit's own process, and the children it spawned that are not sessions (its MCP tool servers).
public sealed record CockpitParts(long OwnBytes, IReadOnlyList<ProcessGroupUsage> Children)
{
    public static readonly CockpitParts None = new(0, []);
}

// One child process tree under the cockpit: what it is, what it holds, and — only while it is the sole process
// carrying that name — the id a caller can match it against (AC-734).
public sealed record ProcessGroupUsage(string Name, long MemoryBytes, int? ProcessId = null);
