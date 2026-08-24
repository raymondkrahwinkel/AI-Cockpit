namespace Cockpit.Core.Diagnostics;

// AC-1013: Local model servers (Ollama, LM Studio) spawn no process a session owns, so they're matched by
// executable name rather than by port — a listening-socket match would buy precision nobody can use — and each
// is measured as a tree; reported apart from the cockpit's own total since they aren't its children (was #78).
public static class LocalModelServers
{
    // Matched against the executable's own name, lower-cased. "ollama" covers the server and its runner; LM Studio
    // ships as an Electron app whose helper processes carry the name too.
    private static readonly (string Match, string DisplayName)[] Known =
    [
        ("ollama", "Ollama"),
        ("lm studio", "LM Studio"),
        ("lmstudio", "LM Studio"),
        ("lms", "LM Studio"),
    ];

    // The model servers running right now, each measured across its whole process tree, heaviest first. A server
    // that is running but holds no model still shows: it is what tells the operator the memory went with the model,
    // not with the server.
    public static IReadOnlyList<ModelServerUsage> From(IReadOnlyList<ProcessRow> rows)
    {
        var byId = rows.ToDictionary(row => row.ProcessId);
        var servers = new Dictionary<string, (long Memory, TimeSpan Cpu)>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (_DisplayNameOf(row.Name) is not { } displayName)
            {
                continue;
            }

            // A child of another process of the same server (ollama's runner under ollama) is already inside its
            // parent's tree, and counting it again would double the model.
            if (byId.TryGetValue(row.ParentProcessId, out var parent) && _DisplayNameOf(parent.Name) == displayName)
            {
                continue;
            }

            var tree = ProcessTree.Sum(rows, row.ProcessId);
            var running = servers.GetValueOrDefault(displayName);
            servers[displayName] = (running.Memory + tree.WorkingSetBytes, running.Cpu + tree.CpuTime);
        }

        return servers
            .Select(server => new ModelServerUsage(server.Key, server.Value.Memory, server.Value.Cpu))
            .OrderByDescending(server => server.MemoryBytes)
            .ToList();
    }

    private static string? _DisplayNameOf(string processName)
    {
        if (string.IsNullOrEmpty(processName))
        {
            return null;
        }

        var name = processName.ToLowerInvariant();

        return Known.FirstOrDefault(known => name.Contains(known.Match, StringComparison.Ordinal)).DisplayName;
    }
}

// One local model server's whole process tree: what it is, and what it is holding.
public sealed record ModelServerUsage(string Name, long MemoryBytes, TimeSpan CpuTime);
