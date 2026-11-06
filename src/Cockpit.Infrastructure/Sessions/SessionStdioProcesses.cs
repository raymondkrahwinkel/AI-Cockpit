using Cockpit.Infrastructure.Mcp;
using Cockpit.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Sessions;

internal sealed class SessionStdioProcesses : ISingletonService
{
    private readonly ILogger _logger;

    public SessionStdioProcesses(ILogger<SessionStdioProcesses> logger, SessionMcpKeyring keyring)
    {
        _logger = logger;
        keyring.RunEnded += paneId => { lock (_sync) { _sessions.Remove(paneId); } };
    }

    private readonly Lock _sync = new();
    private readonly Dictionary<string, int> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _upstreams = [];

    public void Bind(string paneId, int processId)
    {
        lock (_sync) { _sessions[paneId] = processId; }
        Enroll(processId);
    }

    public void Add(string paneId, int processId)
    {
        int root;
        lock (_sync)
        {
            _upstreams[processId] = paneId;
            root = _sessions.GetValueOrDefault(paneId);
        }
        if (root != 0) { Enroll(root); }
    }

    public void Remove(int processId)
    {
        lock (_sync) { _upstreams.Remove(processId); }
    }

    public IReadOnlyList<int> RootsFor(int processId)
    {
        lock (_sync)
        {
            return [.. _upstreams.Where(pair => _sessions.GetValueOrDefault(pair.Value) == processId).Select(pair => pair.Key)];
        }
    }

    public void Enroll(int processId)
    {
        if (!OperatingSystem.IsLinux() || RootsFor(processId) is not { Count: > 0 } roots
            || LinuxSessionCgroup.PathFor(processId) is not { } group
            || !Path.GetFileName(group).Equals(LinuxCgroupMemoryLimiter.GroupNameFor(Environment.ProcessId, processId), StringComparison.Ordinal))
        {
            return;
        }
        var pending = new Stack<int>(roots);
        var seen = new HashSet<int>();
        while (pending.TryPop(out var child))
        {
            if (!seen.Add(child)) { continue; }
            foreach (var descendant in LinuxCgroupMemoryLimiter._ReadChildren(child)) { pending.Push(descendant); }
            try { File.AppendAllText(Path.Combine(group, "cgroup.procs"), child + "\n"); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Session {ProcessId}: stdio process {Child} could not join its memory cgroup.", processId, child);
            }
        }
    }
}
