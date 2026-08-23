using Cockpit.Plugin.Proxmox.Engine;

namespace Cockpit.Plugin.Proxmox.Tests;

// A controllable `IProxmoxEngine` for tests — no real HTTP. `Throw`, when set, is thrown by every call, so a test
// can prove a denied consent never touches the engine at all.
internal sealed class FakeProxmoxEngine : IProxmoxEngine
{
    public Exception? Throw;
    public ProxmoxTaskOutcome NextOutcome = new("UPID:node:0:0:0:task:0:root@pam:", IsSuccess: true, ExitStatus: "OK", TimedOut: false);
    public IReadOnlyList<ProxmoxGuest> Guests = [];
    public IReadOnlyList<ProxmoxSnapshot> Snapshots = [];

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
        return Task.FromResult(Guests.Where(guest => guest.Type == "qemu").ToList() as IReadOnlyList<ProxmoxGuest>);
    }

    public Task<IReadOnlyList<ProxmoxGuest>> ListLxcAsync(CancellationToken cancellationToken)
    {
        _MaybeThrow();
        return Task.FromResult(Guests.Where(guest => guest.Type == "lxc").ToList() as IReadOnlyList<ProxmoxGuest>);
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
        return Task.FromResult(Snapshots);
    }

    public Task<IReadOnlyList<ProxmoxSnapshot>> ListLxcSnapshotsAsync(string node, string vmId, CancellationToken cancellationToken)
    {
        _MaybeThrow();
        return Task.FromResult(Snapshots);
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
}
