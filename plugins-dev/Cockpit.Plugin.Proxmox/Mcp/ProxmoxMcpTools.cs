using System.ComponentModel;
using ModelContextProtocol.Server;
using Cockpit.Plugin.Proxmox.Engine;
using Cockpit.Plugin.Proxmox.Security;
using Cockpit.Plugin.Proxmox.Settings;

namespace Cockpit.Plugin.Proxmox.Mcp;

internal sealed class ProxmoxMcpTools(ProxmoxSettings settings, ProxmoxAccessGate gate, IProxmoxEngine engine)
{
    [McpServerTool(Name = "list_servers", ReadOnly = true)]
    [Description("Lists configured Proxmox servers and whether each has complete connection settings. Tokens are never returned.")]
    public string ListServers() => McpText.Ok(new
    {
        ok = true,
        servers = settings.Servers.Select(server => new { name = server.Name, host = server.Host, reachable = server.IsConfigured && !string.IsNullOrWhiteSpace(settings.GetApiToken(server.Id)) }),
    });

    [McpServerTool(Name = "version", ReadOnly = true)]
    public Task<string> Version(string session, string? server = null, CancellationToken cancellationToken = default) =>
        _ReadAsync(session, server, "read the Proxmox API version", async selected =>
        {
            var version = await selected.GetVersionAsync(cancellationToken);
            return new { ok = true, release = version.Release, repoId = version.RepoId, version = version.Version };
        });

    [McpServerTool(Name = "list_nodes", ReadOnly = true)]
    public Task<string> ListNodes(string session, string? server = null, CancellationToken cancellationToken = default) =>
        _ReadAsync(session, server, "list nodes", async selected =>
        {
            var nodes = await selected.ListNodesAsync(cancellationToken);
            return new { ok = true, count = nodes.Count, nodes = nodes.Select(node => new { node = node.Node, status = node.Status, cpuUsagePercent = node.CpuUsage, maxCpu = node.MaxCpu, memUsedBytes = node.MemUsed, memMaxBytes = node.MemMax, uptimeSeconds = node.Uptime }) };
        });

    [McpServerTool(Name = "cluster_status", ReadOnly = true)]
    public Task<string> ClusterStatus(string session, string? server = null, CancellationToken cancellationToken = default) =>
        _ReadAsync(session, server, "read cluster status", async selected =>
        {
            var info = await selected.GetClusterInfoAsync(cancellationToken);
            return new { ok = true, isCluster = info.IsCluster, name = info.Name, quorate = info.Quorate, nodeCount = info.NodeCount };
        });

    [McpServerTool(Name = "list_vms", ReadOnly = true)]
    public Task<string> ListVms(string session, string? server = null, CancellationToken cancellationToken = default) =>
        _ReadAsync(session, server, "list VMs", async selected =>
        {
            var vms = await selected.ListVmsAsync(cancellationToken);
            return new { ok = true, count = vms.Count, vms = vms.Select(_GuestPayload) };
        });

    [McpServerTool(Name = "list_lxc", ReadOnly = true)]
    public Task<string> ListLxc(string session, string? server = null, CancellationToken cancellationToken = default) =>
        _ReadAsync(session, server, "list LXC containers", async selected =>
        {
            var lxc = await selected.ListLxcAsync(cancellationToken);
            return new { ok = true, count = lxc.Count, lxc = lxc.Select(_GuestPayload) };
        });

    [McpServerTool(Name = "list_storage", ReadOnly = true)]
    public Task<string> ListStorage(string session, string? server = null, CancellationToken cancellationToken = default) =>
        _ReadAsync(session, server, "list storage pools", async selected =>
        {
            var pools = await selected.ListStorageAsync(cancellationToken);
            return new { ok = true, count = pools.Count, storage = pools.Select(pool => new { storage = pool.Storage, node = pool.Node, type = pool.Type, totalBytes = pool.TotalBytes, usedBytes = pool.UsedBytes, enabled = pool.Enabled }) };
        });

    [McpServerTool(Name = "list_tasks", ReadOnly = true)]
    public Task<string> ListTasks(string session, string node, string? server = null, CancellationToken cancellationToken = default) =>
        _ReadAsync(session, server, $"list tasks on node \"{node}\"", async selected =>
        {
            var tasks = await selected.ListTasksAsync(node, cancellationToken);
            return new { ok = true, count = tasks.Count, tasks = tasks.Select(task => new { upid = task.Upid, type = task.Type, status = task.Status, user = task.User, startTime = task.StartTime, endTime = task.EndTime }) };
        });

    [McpServerTool(Name = "list_vm_snapshots", ReadOnly = true)]
    public Task<string> ListVmSnapshots(string session, string node, string vmid, string? server = null, CancellationToken cancellationToken = default) =>
        _SnapshotsAsync(session, node, vmid, false, server, cancellationToken);

    [McpServerTool(Name = "list_lxc_snapshots", ReadOnly = true)]
    public Task<string> ListLxcSnapshots(string session, string node, string vmid, string? server = null, CancellationToken cancellationToken = default) =>
        _SnapshotsAsync(session, node, vmid, true, server, cancellationToken);

    [McpServerTool(Name = "start_vm", ReadOnly = false, Destructive = true)]
    public Task<string> StartVm(string session, string node, string vmid, string? server = null, CancellationToken cancellationToken = default) =>
        _MutateAsync(session, server, ProxmoxActionText.StartVm(node, vmid), selected => selected.StartVmAsync(node, vmid, cancellationToken));

    [McpServerTool(Name = "shutdown_vm", ReadOnly = false, Destructive = true)]
    public Task<string> ShutdownVm(string session, string node, string vmid, string? server = null, CancellationToken cancellationToken = default) =>
        _MutateAsync(session, server, ProxmoxActionText.ShutdownVm(node, vmid), selected => selected.ShutdownVmAsync(node, vmid, cancellationToken));

    [McpServerTool(Name = "stop_vm", ReadOnly = false, Destructive = true)]
    public Task<string> StopVm(string session, string node, string vmid, string? server = null, CancellationToken cancellationToken = default) =>
        _MutateAsync(session, server, ProxmoxActionText.StopVm(node, vmid), selected => selected.StopVmAsync(node, vmid, cancellationToken));

    [McpServerTool(Name = "reboot_vm", ReadOnly = false, Destructive = true)]
    public Task<string> RebootVm(string session, string node, string vmid, string? server = null, CancellationToken cancellationToken = default) =>
        _MutateAsync(session, server, ProxmoxActionText.RebootVm(node, vmid), selected => selected.RebootVmAsync(node, vmid, cancellationToken));

    [McpServerTool(Name = "snapshot_vm", ReadOnly = false, Destructive = false)]
    public Task<string> SnapshotVm(string session, string node, string vmid, string name, string? description = null, string? server = null, CancellationToken cancellationToken = default) =>
        _MutateAsync(session, server, ProxmoxActionText.SnapshotVm(node, vmid, name), selected => selected.SnapshotVmAsync(node, vmid, name, description, cancellationToken));

    [McpServerTool(Name = "start_lxc", ReadOnly = false, Destructive = true)]
    public Task<string> StartLxc(string session, string node, string vmid, string? server = null, CancellationToken cancellationToken = default) =>
        _MutateAsync(session, server, ProxmoxActionText.StartLxc(node, vmid), selected => selected.StartLxcAsync(node, vmid, cancellationToken));

    [McpServerTool(Name = "shutdown_lxc", ReadOnly = false, Destructive = true)]
    public Task<string> ShutdownLxc(string session, string node, string vmid, string? server = null, CancellationToken cancellationToken = default) =>
        _MutateAsync(session, server, ProxmoxActionText.ShutdownLxc(node, vmid), selected => selected.ShutdownLxcAsync(node, vmid, cancellationToken));

    [McpServerTool(Name = "stop_lxc", ReadOnly = false, Destructive = true)]
    public Task<string> StopLxc(string session, string node, string vmid, string? server = null, CancellationToken cancellationToken = default) =>
        _MutateAsync(session, server, ProxmoxActionText.StopLxc(node, vmid), selected => selected.StopLxcAsync(node, vmid, cancellationToken));

    [McpServerTool(Name = "reboot_lxc", ReadOnly = false, Destructive = true)]
    public Task<string> RebootLxc(string session, string node, string vmid, string? server = null, CancellationToken cancellationToken = default) =>
        _MutateAsync(session, server, ProxmoxActionText.RebootLxc(node, vmid), selected => selected.RebootLxcAsync(node, vmid, cancellationToken));

    [McpServerTool(Name = "snapshot_lxc", ReadOnly = false, Destructive = false)]
    public Task<string> SnapshotLxc(string session, string node, string vmid, string name, string? description = null, string? server = null, CancellationToken cancellationToken = default) =>
        _MutateAsync(session, server, ProxmoxActionText.SnapshotLxc(node, vmid, name), selected => selected.SnapshotLxcAsync(node, vmid, name, description, cancellationToken));

    [McpServerTool(Name = "rollback_vm_snapshot", ReadOnly = false, Destructive = true)]
    public Task<string> RollbackVmSnapshot(string session, string node, string vmid, string name, string? server = null, CancellationToken cancellationToken = default) =>
        _DangerAsync(session, server, DangerCapability.Rollback, target => target.AllowRollback, ProxmoxActionText.RollbackVm(node, vmid, name), selected => selected.RollbackVmSnapshotAsync(node, vmid, name, cancellationToken));

    [McpServerTool(Name = "rollback_lxc_snapshot", ReadOnly = false, Destructive = true)]
    public Task<string> RollbackLxcSnapshot(string session, string node, string vmid, string name, string? server = null, CancellationToken cancellationToken = default) =>
        _DangerAsync(session, server, DangerCapability.Rollback, target => target.AllowRollback, ProxmoxActionText.RollbackLxc(node, vmid, name), selected => selected.RollbackLxcSnapshotAsync(node, vmid, name, cancellationToken));

    [McpServerTool(Name = "delete_vm", ReadOnly = false, Destructive = true)]
    public Task<string> DeleteVm(string session, string node, string vmid, string? server = null, CancellationToken cancellationToken = default) =>
        _DangerAsync(session, server, DangerCapability.Delete, target => target.AllowDelete, ProxmoxActionText.DeleteVm(node, vmid), selected => selected.DeleteVmAsync(node, vmid, cancellationToken));

    [McpServerTool(Name = "delete_lxc", ReadOnly = false, Destructive = true)]
    public Task<string> DeleteLxc(string session, string node, string vmid, string? server = null, CancellationToken cancellationToken = default) =>
        _DangerAsync(session, server, DangerCapability.Delete, target => target.AllowDelete, ProxmoxActionText.DeleteLxc(node, vmid), selected => selected.DeleteLxcAsync(node, vmid, cancellationToken));

    private async Task<string> _ReadAsync(string session, string? server, string operation, Func<IProxmoxEngine, Task<object>> read)
    {
        var target = _Target(server, out var error);
        if (target is null)
        {
            return error!;
        }

        var decision = await gate.AuthorizeConnectionAsync($"{operation} on Proxmox server \"{target.Name}\"", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            return McpText.Ok(await read(engine.ForServer(target)));
        }
        catch (Exception ex)
        {
            return _Failure(ex);
        }
    }

    private Task<string> _SnapshotsAsync(string session, string node, string vmid, bool isLxc, string? server, CancellationToken cancellationToken) =>
        _ReadAsync(session, server, $"list snapshots of {(isLxc ? "LXC container" : "VM")} {vmid} on node \"{node}\"", async selected =>
        {
            var snapshots = isLxc ? await selected.ListLxcSnapshotsAsync(node, vmid, cancellationToken) : await selected.ListVmSnapshotsAsync(node, vmid, cancellationToken);
            return new { ok = true, count = snapshots.Count, snapshots = snapshots.Select(snapshot => new { name = snapshot.Name, description = snapshot.Description, snapTime = snapshot.SnapTime, isCurrent = snapshot.IsCurrent }) };
        });

    private async Task<string> _MutateAsync(string session, string? server, string operation, Func<IProxmoxEngine, Task<ProxmoxTaskOutcome>> action)
    {
        var target = _Target(server, out var error);
        if (target is null)
        {
            return error!;
        }

        var decision = await gate.AuthorizeMutationAsync($"{operation} on Proxmox server \"{target.Name}\"", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            return _TaskResult(await action(engine.ForServer(target)));
        }
        catch (Exception ex)
        {
            return _Failure(ex);
        }
    }

    private async Task<string> _DangerAsync(string session, string? server, DangerCapability capability, Func<ProxmoxServer, bool> enabled, string operation, Func<IProxmoxEngine, Task<ProxmoxTaskOutcome>> action)
    {
        var target = _Target(server, out var error);
        if (target is null)
        {
            return error!;
        }

        var decision = await gate.AuthorizeDangerAsync(capability, enabled(target), $"{operation} on Proxmox server \"{target.Name}\"", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            return _TaskResult(await action(engine.ForServer(target)));
        }
        catch (Exception ex)
        {
            return _Failure(ex);
        }
    }

    private ProxmoxServer? _Target(string? server, out string? error)
    {
        try
        {
            error = null;
            return settings.ResolveServer(server);
        }
        catch (Exception ex)
        {
            error = _Failure(ex);
            return null;
        }
    }

    private static string _TaskResult(ProxmoxTaskOutcome outcome) => outcome.TimedOut
        ? McpText.Error($"The task (upid={outcome.Upid}) is still running and was not confirmed to finish. Check its status with list_tasks.")
        : outcome.IsSuccess
            ? McpText.Ok(new { ok = true, upid = outcome.Upid, exitStatus = outcome.ExitStatus })
            : McpText.Error($"The task (upid={outcome.Upid}) did not succeed: {outcome.ExitStatus}");

    private static object _GuestPayload(ProxmoxGuest guest) => new { vmid = guest.VmId, name = guest.Name, node = guest.Node, status = guest.Status, maxMemBytes = guest.MaxMem, maxDiskBytes = guest.MaxDisk, maxCpu = guest.MaxCpu, uptimeSeconds = guest.Uptime };

    private static string _Failure(Exception ex) => ex switch
    {
        OperationCanceledException => McpText.Error("The operation was cancelled."),
        ProxmoxApiException apiEx => McpText.Error(apiEx.Message),
        _ => McpText.Error($"The Proxmox request failed ({ex.GetType().Name})."),
    };
}
