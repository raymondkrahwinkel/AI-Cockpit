using Cockpit.Infrastructure.Mcp;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Sessions;

internal sealed class SessionStdioProcesses : ISingletonService
{
    private readonly IProcessTableReader _reader;
    private readonly ILogger _logger;

    public SessionStdioProcesses(IProcessTableReader reader, ILogger<SessionStdioProcesses> logger, SessionMcpKeyring keyring)
    {
        _reader = reader;
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
        foreach (var child in ProcessTree.Snapshot(_reader.Read()).LiveReachableFrom(roots))
        {
            try { File.AppendAllText(Path.Combine(group, "cgroup.procs"), child + "\n"); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Session {ProcessId}: stdio process {Child} could not join its memory cgroup.", processId, child);
            }
        }
    }
}
