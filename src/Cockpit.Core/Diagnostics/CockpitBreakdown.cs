namespace Cockpit.Core.Diagnostics;

/// <summary>
/// What the cockpit's own total is actually made of (#78). The figure in the status bar is the whole process tree, and
/// a session is only one of the things in it: the MCP tool servers a session connects to (<c>npm exec …</c>, <c>uv</c>)
/// are spawned by the cockpit, so they are counted in that total — while appearing nowhere, being neither a session nor
/// a model server. That is how opening one Ollama session takes the figure from 300 MB to 800 with no explanation on
/// screen, and a number nobody can explain is a number nobody can act on.
/// <para>
/// Each child is measured as a tree (an <c>npm exec</c> is a shell around the node process doing the work), and the
/// sessions are left out: they have a section of their own, and counting them twice would make the parts add up to
/// more than the whole.
/// </para>
/// </summary>
public static class CockpitBreakdown
{
    /// <summary>
    /// The cockpit's own process, and each of its children that is not a session, heaviest first. Together with the
    /// sessions these are the total — which is what makes the total explicable.
    /// </summary>
    public static CockpitParts From(IReadOnlyList<ProcessRow> rows, int cockpitProcessId, IReadOnlyCollection<int> sessionProcessIds)
    {
        var own = rows.FirstOrDefault(row => row.ProcessId == cockpitProcessId)?.WorkingSetBytes ?? 0;

        var children = rows
            .Where(row => row.ParentProcessId == cockpitProcessId && !sessionProcessIds.Contains(row.ProcessId))
            .Select(row => new ProcessGroupUsage(row.Name, ProcessTree.Sum(rows, row.ProcessId).WorkingSetBytes))
            // Two MCP servers started the same way carry the same name; they are one line, because "npm exec" twice
            // over is not a thing the operator can tell apart or act on separately.
            .GroupBy(child => child.Name, StringComparer.Ordinal)
            .Select(group => new ProcessGroupUsage(
                group.Count() == 1 ? group.Key : $"{group.Key} ×{group.Count()}",
                group.Sum(child => child.MemoryBytes)))
            .OrderByDescending(child => child.MemoryBytes)
            .ToList();

        return new CockpitParts(own, children);
    }
}

/// <summary>The cockpit's own process, and the children it spawned that are not sessions (its MCP tool servers).</summary>
public sealed record CockpitParts(long OwnBytes, IReadOnlyList<ProcessGroupUsage> Children)
{
    public static readonly CockpitParts None = new(0, []);
}

/// <summary>One child process tree under the cockpit: what it is, and what it holds.</summary>
public sealed record ProcessGroupUsage(string Name, long MemoryBytes);
