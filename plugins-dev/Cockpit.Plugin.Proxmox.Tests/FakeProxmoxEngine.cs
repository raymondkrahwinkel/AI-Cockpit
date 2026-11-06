using Cockpit.Plugin.Proxmox.Engine;
using Cockpit.Plugin.Proxmox.Settings;

namespace Cockpit.Plugin.Proxmox.Tests;

// A controllable `IProxmoxEngine` for tests — no real HTTP. `Throw`, when set, is thrown by every call, so a test
// can prove a denied consent never touches the engine at all.
internal sealed class FakeProxmoxEngine : IProxmoxEngine
{
    public Exception? Throw;
    public ProxmoxTaskOutcome NextOutcome = new("UPID:node:0:0:0:task:0:root@pam:", IsSuccess: true, ExitStatus: "OK", TimedOut: false);

    public IProxmoxEngine ForServer(ProxmoxServer server) => this;

    private void _MaybeThrow()
    {
        if (Throw is not null)
        {
            throw Throw;
        }
    }

    public Task<ProxmoxVersion> GetVersionAsync(CancellationToken cancellationToken)
    {
        _MaybeThrow();
        return Task.FromResult(new ProxmoxVersion("8.2", "abcdef12", "8.2.4"));
    }

    public Task<IReadOnlyList<ProxmoxNode>> ListNodesAsync(CancellationToken cancellationToken)
    {
        _MaybeThrow();
        return Task.FromResult<IReadOnlyList<ProxmoxNode>>([]);
    }

    public Task<ProxmoxClusterInfo> GetClusterInfoAsync(CancellationToken cancellationToken)
    {
        _MaybeThrow();
        return Task.FromResult(new ProxmoxClusterInfo(false, null, true, 1));
    }

    public Task<IReadOnlyList<ProxmoxGuest>> ListVmsAsync(CancellationToken cancellationToken)
    {
        _MaybeThrow();
        return Task.FromResult<IReadOnlyList<ProxmoxGuest>>([]);
    }

    public Task<IReadOnlyList<ProxmoxGuest>> ListLxcAsync(CancellationToken cancellationToken)
    {
        _MaybeThrow();
        return Task.FromResult<IReadOnlyList<ProxmoxGuest>>([]);
    }

    private Task<ProxmoxTaskOutcome> _Outcome()
    {
        _MaybeThrow();
        return Task.FromResult(NextOutcome);
    }

    public Task<ProxmoxTaskOutcome> StartVmAsync(string node, string vmId, CancellationToken cancellationToken) => _Outcome();

    public Task<ProxmoxTaskOutcome> ShutdownVmAsync(string node, string vmId, CancellationToken cancellationToken) => _Outcome();

    public Task<ProxmoxTaskOutcome> StopVmAsync(string node, string vmId, CancellationToken cancellationToken) => _Outcome();

    public Task<ProxmoxTaskOutcome> RebootVmAsync(string node, string vmId, CancellationToken cancellationToken) => _Outcome();

    public Task<ProxmoxTaskOutcome> DeleteVmAsync(string node, string vmId, CancellationToken cancellationToken) => _Outcome();

    public Task<ProxmoxTaskOutcome> StartLxcAsync(string node, string vmId, CancellationToken cancellationToken) => _Outcome();

    public Task<ProxmoxTaskOutcome> ShutdownLxcAsync(string node, string vmId, CancellationToken cancellationToken) => _Outcome();

    public Task<ProxmoxTaskOutcome> StopLxcAsync(string node, string vmId, CancellationToken cancellationToken) => _Outcome();

    public Task<ProxmoxTaskOutcome> RebootLxcAsync(string node, string vmId, CancellationToken cancellationToken) => _Outcome();

    public Task<ProxmoxTaskOutcome> DeleteLxcAsync(string node, string vmId, CancellationToken cancellationToken) => _Outcome();

    public Task<IReadOnlyList<ProxmoxSnapshot>> ListVmSnapshotsAsync(string node, string vmId, CancellationToken cancellationToken)
    {
        _MaybeThrow();
        return Task.FromResult<IReadOnlyList<ProxmoxSnapshot>>([]);
    }

    public Task<IReadOnlyList<ProxmoxSnapshot>> ListLxcSnapshotsAsync(string node, string vmId, CancellationToken cancellationToken)
    {
        _MaybeThrow();
        return Task.FromResult<IReadOnlyList<ProxmoxSnapshot>>([]);
    }

    public Task<ProxmoxTaskOutcome> SnapshotVmAsync(string node, string vmId, string name, string? description, CancellationToken cancellationToken) => _Outcome();

    public Task<ProxmoxTaskOutcome> SnapshotLxcAsync(string node, string vmId, string name, string? description, CancellationToken cancellationToken) => _Outcome();

    public Task<ProxmoxTaskOutcome> RollbackVmSnapshotAsync(string node, string vmId, string name, CancellationToken cancellationToken) => _Outcome();

    public Task<ProxmoxTaskOutcome> RollbackLxcSnapshotAsync(string node, string vmId, string name, CancellationToken cancellationToken) => _Outcome();

    public Task<IReadOnlyList<ProxmoxStoragePool>> ListStorageAsync(CancellationToken cancellationToken)
    {
        _MaybeThrow();
        return Task.FromResult<IReadOnlyList<ProxmoxStoragePool>>([]);
    }

    public Task<IReadOnlyList<ProxmoxTaskSummary>> ListTasksAsync(string node, CancellationToken cancellationToken)
    {
        _MaybeThrow();
        return Task.FromResult<IReadOnlyList<ProxmoxTaskSummary>>([]);
    }

    public Task<IReadOnlyList<PbsDatastore>> ListPbsDatastoresAsync(CancellationToken cancellationToken)
    {
        _MaybeThrow();
        return Task.FromResult<IReadOnlyList<PbsDatastore>>([]);
    }

    public Task<IReadOnlyList<PbsBackup>> ListPbsBackupsAsync(string datastore, string? type, string? id, CancellationToken cancellationToken)
    {
        _MaybeThrow();
        return Task.FromResult<IReadOnlyList<PbsBackup>>([]);
    }

    public Task<IReadOnlyList<PbsTask>> ListPbsTasksAsync(CancellationToken cancellationToken)
    {
        _MaybeThrow();
        return Task.FromResult<IReadOnlyList<PbsTask>>([]);
    }

    public Task<IReadOnlyList<PbsJob>> ListPbsJobsAsync(CancellationToken cancellationToken)
    {
        _MaybeThrow();
        return Task.FromResult<IReadOnlyList<PbsJob>>([]);
    }

    public Task<ProxmoxTaskOutcome> StartPbsVerifyJobAsync(string id, CancellationToken cancellationToken) => _Outcome();

    public Task<ProxmoxTaskOutcome> StartPbsGcAsync(string datastore, CancellationToken cancellationToken) => _Outcome();
}
