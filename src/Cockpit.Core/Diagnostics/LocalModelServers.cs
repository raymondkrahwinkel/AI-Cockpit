namespace Cockpit.Core.Diagnostics;

/// <summary>
/// The local model servers on this machine (#78): Ollama, LM Studio. A session that talks to one of these over HTTP
/// spawns no process of its own, which is why the breakdown had nothing to say about it — but the model is still the
/// heaviest thing on the machine, and the operator asking "what is using my memory" means that too.
/// <para>
/// They are found by name rather than by which port a session points at: one server answers every session, so there
/// is nothing to attribute to a session anyway, and matching a listening socket to a process would buy precision
/// nobody can use. Named by what they are — <c>ollama</c> keeps its model in a child process (<c>ollama runner</c>),
/// so each server is measured as a tree, exactly like a session is.
/// </para>
/// <para>
/// Deliberately reported apart from the cockpit's own total: these processes are not the cockpit's children, they
/// outlive its sessions, and adding them to a figure the operator reads as "what this app costs" would be a lie.
/// </para>
/// </summary>
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

    /// <summary>
    /// The model servers running right now, each measured across its whole process tree, heaviest first. A server
    /// that is running but holds no model still shows: it is what tells the operator the memory went with the model,
    /// not with the server.
    /// </summary>
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

/// <summary>One local model server's whole process tree: what it is, and what it is holding.</summary>
public sealed record ModelServerUsage(string Name, long MemoryBytes, TimeSpan CpuTime);
