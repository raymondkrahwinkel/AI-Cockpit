using System.ComponentModel;
using ModelContextProtocol.Server;
using Cockpit.Plugin.Proxmox.Engine;
using Cockpit.Plugin.Proxmox.Security;
using Cockpit.Plugin.Proxmox.Settings;

namespace Cockpit.Plugin.Proxmox.Mcp;

internal sealed class ProxmoxMcpTools(ProxmoxSettings settings, ProxmoxAccessGate gate, IProxmoxEngine engine)
{
    private const string SessionDescription = "Your session id (COCKPIT_PANE_ID).";
    private const string ServerDescription = "Name from list_servers; omit only when exactly one server is configured.";

    [McpServerTool(Name = "list_servers", ReadOnly = true)]
    [Description("Lists configured Proxmox servers and whether each has complete connection settings. Tokens are never returned; configured does not probe the network.")]
    public string ListServers() => McpText.Ok(new
    {
        ok = true,
        servers = settings.Servers.Select(server => new { name = server.Name, host = server.Host, kind = server.Kind.ToString().ToLowerInvariant(), configured = server.IsConfigured && !string.IsNullOrWhiteSpace(settings.GetApiToken(server.Id)) }),
    });

    [McpServerTool(Name = "pbs_list_datastores", ReadOnly = true)]
    [Description("Lists PBS datastores with bytes used/free and their GC and verification status. A read behind one-time connection consent for the selected PBS server.")]
    public Task<string> PbsListDatastores([Description(SessionDescription)] string session, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _PbsReadAsync(session, server, "list PBS datastores", async selected =>
        {
            var stores = await selected.ListPbsDatastoresAsync(cancellationToken);
            return new { ok = true, count = stores.Count, datastores = stores.Select(store => new { name = store.Name, totalBytes = store.TotalBytes, usedBytes = store.UsedBytes, freeBytes = Math.Max(0, store.TotalBytes - store.UsedBytes), gcStatus = store.GcStatus, verifyStatus = store.VerifyStatus }) };
        });

    [McpServerTool(Name = "pbs_list_backups", ReadOnly = true)]
    [Description("Lists PBS backup snapshots in one datastore, optionally filtered to a backup type and id such as vm and 101. A read behind one-time connection consent for the selected PBS server.")]
    public Task<string> PbsListBackups([Description(SessionDescription)] string session, [Description("PBS datastore name.")] string datastore, [Description("Optional backup type, for example vm.")] string? type = null, [Description("Optional backup id, for example 101.")] string? id = null, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _PbsReadAsync(session, server, $"list PBS backups in datastore \"{datastore}\"", async selected =>
        {
            var backups = await selected.ListPbsBackupsAsync(datastore, type, id, cancellationToken);
            return new { ok = true, count = backups.Count, backups = backups.Select(backup => new { datastore = backup.Datastore, type = backup.Type, id = backup.Id, backupTime = backup.BackupTime, sizeBytes = backup.SizeBytes, verification = backup.VerifyState, @protected = backup.Protected }) };
        });

    [McpServerTool(Name = "pbs_list_tasks", ReadOnly = true)]
    [Description("Lists recent PBS tasks including backup, verify, prune, garbage collection and sync work. A read behind one-time connection consent for the selected PBS server.")]
    public Task<string> PbsListTasks([Description(SessionDescription)] string session, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _PbsReadAsync(session, server, "list PBS tasks", async selected =>
        {
            var tasks = await selected.ListPbsTasksAsync(cancellationToken);
            return new { ok = true, count = tasks.Count, tasks = tasks.Select(task => new { upid = task.Upid, type = task.Type, status = task.Status, user = task.User, startTime = task.StartTime, endTime = task.EndTime }) };
        });

    [McpServerTool(Name = "pbs_list_jobs", ReadOnly = true)]
    [Description("Lists configured PBS verify, prune and sync jobs with their schedule and last outcome. A read behind one-time connection consent for the selected PBS server.")]
    public Task<string> PbsListJobs([Description(SessionDescription)] string session, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _PbsReadAsync(session, server, "list PBS jobs", async selected =>
        {
            var jobs = await selected.ListPbsJobsAsync(cancellationToken);
            return new { ok = true, count = jobs.Count, jobs = jobs.Select(job => new { type = job.Type, id = job.Id, datastore = job.Datastore, schedule = job.Schedule, lastRunState = job.LastRunState }) };
        });

    [McpServerTool(Name = "pbs_start_verify_job", ReadOnly = false, Destructive = false)]
    [Description("Starts an existing PBS verification job. Every start asks the operator afresh with the literal job and server, and is never remembered.")]
    public Task<string> PbsStartVerifyJob([Description(SessionDescription)] string session, [Description("Existing PBS verification job id.")] string id, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _PbsMutateAsync(session, server, $"start PBS verify job \"{id}\"", selected => selected.StartPbsVerifyJobAsync(id, cancellationToken));

    [McpServerTool(Name = "pbs_start_gc", ReadOnly = false, Destructive = false)]
    [Description("Starts PBS garbage collection for one datastore. Every start asks the operator afresh with the literal datastore and server, and is never remembered.")]
    public Task<string> PbsStartGc([Description(SessionDescription)] string session, [Description("PBS datastore name.")] string datastore, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _PbsMutateAsync(session, server, $"start PBS garbage collection for datastore \"{datastore}\"", selected => selected.StartPbsGcAsync(datastore, cancellationToken));

    [McpServerTool(Name = "version", ReadOnly = true)]
    [Description("Returns the Proxmox VE API's version and release. This is the first call that touches the selected API, so it asks the operator for consent once per server; after that, reads are free for that server in the session. Start here to confirm the target is reachable.")]
    public Task<string> Version([Description(SessionDescription)] string session, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _ReadAsync(session, server, "read the Proxmox API version", async selected =>
        {
            var version = await selected.GetVersionAsync(cancellationToken);
            return new { ok = true, release = version.Release, repoId = version.RepoId, version = version.Version };
        });

    [McpServerTool(Name = "list_nodes", ReadOnly = true)]
    [Description("Lists the nodes in this Proxmox target with their status, CPU usage and memory usage. A read behind the one-time connection consent for the selected server.")]
    public Task<string> ListNodes([Description(SessionDescription)] string session, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _ReadAsync(session, server, "list nodes", async selected =>
        {
            var nodes = await selected.ListNodesAsync(cancellationToken);
            return new { ok = true, count = nodes.Count, nodes = nodes.Select(node => new { node = node.Node, status = node.Status, cpuUsagePercent = node.CpuUsage, maxCpu = node.MaxCpu, memUsedBytes = node.MemUsed, memMaxBytes = node.MemMax, uptimeSeconds = node.Uptime }) };
        });

    [McpServerTool(Name = "cluster_status", ReadOnly = true)]
    [Description("Reports whether this target is a Proxmox cluster and, if so, its name, quorum state and node count. A single host reports isCluster=false. A read behind the one-time connection consent for the selected server.")]
    public Task<string> ClusterStatus([Description(SessionDescription)] string session, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _ReadAsync(session, server, "read cluster status", async selected =>
        {
            var info = await selected.GetClusterInfoAsync(cancellationToken);
            return new { ok = true, isCluster = info.IsCluster, name = info.Name, quorate = info.Quorate, nodeCount = info.NodeCount };
        });

    [McpServerTool(Name = "list_vms", ReadOnly = true)]
    [Description("Lists every QEMU VM across the selected target: id, name, node, status, assigned memory/disk/CPU and uptime. A read behind the one-time connection consent for that server.")]
    public Task<string> ListVms([Description(SessionDescription)] string session, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _ReadAsync(session, server, "list VMs", async selected =>
        {
            var vms = await selected.ListVmsAsync(cancellationToken);
            return new { ok = true, count = vms.Count, vms = vms.Select(_GuestPayload) };
        });

    [McpServerTool(Name = "list_lxc", ReadOnly = true)]
    [Description("Lists every LXC container across the selected target: id, name, node, status, assigned memory/disk/CPU and uptime. These are Proxmox LXC containers, distinct from Docker containers. A read behind the one-time connection consent for that server.")]
    public Task<string> ListLxc([Description(SessionDescription)] string session, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _ReadAsync(session, server, "list LXC containers", async selected =>
        {
            var lxc = await selected.ListLxcAsync(cancellationToken);
            return new { ok = true, count = lxc.Count, lxc = lxc.Select(_GuestPayload) };
        });

    [McpServerTool(Name = "list_storage", ReadOnly = true)]
    [Description("Lists storage pools across the selected target with total and used bytes. A read behind the one-time connection consent for that server.")]
    public Task<string> ListStorage([Description(SessionDescription)] string session, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _ReadAsync(session, server, "list storage pools", async selected =>
        {
            var pools = await selected.ListStorageAsync(cancellationToken);
            return new { ok = true, count = pools.Count, storage = pools.Select(pool => new { storage = pool.Storage, node = pool.Node, type = pool.Type, totalBytes = pool.TotalBytes, usedBytes = pool.UsedBytes, enabled = pool.Enabled }) };
        });

    [McpServerTool(Name = "list_tasks", ReadOnly = true)]
    [Description("Lists a node's recent tasks — what ran or is running, with its outcome. Use this to follow up on a task that timed out while waiting. A read behind the one-time connection consent for the selected server.")]
    public Task<string> ListTasks([Description(SessionDescription)] string session, [Description("The node name, as returned by list_nodes.")] string node, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _ReadAsync(session, server, $"list tasks on node \"{node}\"", async selected =>
        {
            var tasks = await selected.ListTasksAsync(node, cancellationToken);
            return new { ok = true, count = tasks.Count, tasks = tasks.Select(task => new { upid = task.Upid, type = task.Type, status = task.Status, user = task.User, startTime = task.StartTime, endTime = task.EndTime }) };
        });

    [McpServerTool(Name = "list_vm_snapshots", ReadOnly = true)]
    [Description("Lists a VM's snapshots: name, description and creation time. A read behind the one-time connection consent for the selected server.")]
    public Task<string> ListVmSnapshots([Description(SessionDescription)] string session, [Description("The node the VM runs on.")] string node, [Description("The VM id.")] string vmid, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _SnapshotsAsync(session, node, vmid, false, server, cancellationToken);

    [McpServerTool(Name = "list_lxc_snapshots", ReadOnly = true)]
    [Description("Lists an LXC container's snapshots: name, description and creation time. A read behind the one-time connection consent for the selected server.")]
    public Task<string> ListLxcSnapshots([Description(SessionDescription)] string session, [Description("The node the LXC container runs on.")] string node, [Description("The LXC container id.")] string vmid, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _SnapshotsAsync(session, node, vmid, true, server, cancellationToken);

    [McpServerTool(Name = "start_vm", ReadOnly = false, Destructive = true)]
    [Description("Starts a stopped VM. Waits for the Proxmox task to finish and reports its real outcome. This is a change, so it asks the operator afresh each time and is never remembered.")]
    public Task<string> StartVm([Description(SessionDescription)] string session, [Description("The node the VM runs on.")] string node, [Description("The VM id.")] string vmid, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _MutateAsync(session, server, ProxmoxActionText.StartVm(node, vmid), selected => selected.StartVmAsync(node, vmid, cancellationToken));

    [McpServerTool(Name = "shutdown_vm", ReadOnly = false, Destructive = true)]
    [Description("Gracefully shuts a VM down (an ACPI shutdown request the guest OS can act on) — distinct from stop_vm, which powers it off immediately. Waits for the Proxmox task to finish and reports its real outcome. Asks the operator afresh each time and is never remembered.")]
    public Task<string> ShutdownVm([Description(SessionDescription)] string session, [Description("The node the VM runs on.")] string node, [Description("The VM id.")] string vmid, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _MutateAsync(session, server, ProxmoxActionText.ShutdownVm(node, vmid), selected => selected.ShutdownVmAsync(node, vmid, cancellationToken));

    [McpServerTool(Name = "stop_vm", ReadOnly = false, Destructive = true)]
    [Description("Hard-powers a VM off immediately, with no guest cooperation — distinct from shutdown_vm's graceful ACPI request. Like pulling the power cord: unsaved guest state can be lost. Waits for the Proxmox task to finish and reports its real outcome. Asks the operator afresh each time and is never remembered.")]
    public Task<string> StopVm([Description(SessionDescription)] string session, [Description("The node the VM runs on.")] string node, [Description("The VM id.")] string vmid, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _MutateAsync(session, server, ProxmoxActionText.StopVm(node, vmid), selected => selected.StopVmAsync(node, vmid, cancellationToken));

    [McpServerTool(Name = "reboot_vm", ReadOnly = false, Destructive = true)]
    [Description("Gracefully reboots a VM (ACPI). Waits for the Proxmox task to finish and reports its real outcome. Asks the operator afresh each time and is never remembered.")]
    public Task<string> RebootVm([Description(SessionDescription)] string session, [Description("The node the VM runs on.")] string node, [Description("The VM id.")] string vmid, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _MutateAsync(session, server, ProxmoxActionText.RebootVm(node, vmid), selected => selected.RebootVmAsync(node, vmid, cancellationToken));

    [McpServerTool(Name = "snapshot_vm", ReadOnly = false, Destructive = false)]
    [Description("Creates a snapshot of a VM. Waits for the Proxmox task to finish and reports its real outcome. Asks the operator afresh each time and is never remembered.")]
    public Task<string> SnapshotVm([Description(SessionDescription)] string session, [Description("The node the VM runs on.")] string node, [Description("The VM id.")] string vmid, [Description("The snapshot's name.")] string name, [Description("Optional snapshot description.")] string? description = null, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _MutateAsync(session, server, ProxmoxActionText.SnapshotVm(node, vmid, name), selected => selected.SnapshotVmAsync(node, vmid, name, description, cancellationToken));

    [McpServerTool(Name = "start_lxc", ReadOnly = false, Destructive = true)]
    [Description("Starts a stopped LXC container. Waits for the Proxmox task to finish and reports its real outcome. Asks the operator afresh each time and is never remembered.")]
    public Task<string> StartLxc([Description(SessionDescription)] string session, [Description("The node the LXC container runs on.")] string node, [Description("The LXC container id.")] string vmid, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _MutateAsync(session, server, ProxmoxActionText.StartLxc(node, vmid), selected => selected.StartLxcAsync(node, vmid, cancellationToken));

    [McpServerTool(Name = "shutdown_lxc", ReadOnly = false, Destructive = true)]
    [Description("Gracefully shuts an LXC container down — distinct from stop_lxc, which stops it immediately. Waits for the Proxmox task to finish and reports its real outcome. Asks the operator afresh each time and is never remembered.")]
    public Task<string> ShutdownLxc([Description(SessionDescription)] string session, [Description("The node the LXC container runs on.")] string node, [Description("The LXC container id.")] string vmid, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _MutateAsync(session, server, ProxmoxActionText.ShutdownLxc(node, vmid), selected => selected.ShutdownLxcAsync(node, vmid, cancellationToken));

    [McpServerTool(Name = "stop_lxc", ReadOnly = false, Destructive = true)]
    [Description("Stops an LXC container immediately, with no cooperation from the processes inside it — distinct from shutdown_lxc's graceful stop. Waits for the Proxmox task to finish and reports its real outcome. Asks the operator afresh each time and is never remembered.")]
    public Task<string> StopLxc([Description(SessionDescription)] string session, [Description("The node the LXC container runs on.")] string node, [Description("The LXC container id.")] string vmid, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _MutateAsync(session, server, ProxmoxActionText.StopLxc(node, vmid), selected => selected.StopLxcAsync(node, vmid, cancellationToken));

    [McpServerTool(Name = "reboot_lxc", ReadOnly = false, Destructive = true)]
    [Description("Reboots an LXC container. Waits for the Proxmox task to finish and reports its real outcome. Asks the operator afresh each time and is never remembered.")]
    public Task<string> RebootLxc([Description(SessionDescription)] string session, [Description("The node the LXC container runs on.")] string node, [Description("The LXC container id.")] string vmid, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _MutateAsync(session, server, ProxmoxActionText.RebootLxc(node, vmid), selected => selected.RebootLxcAsync(node, vmid, cancellationToken));

    [McpServerTool(Name = "snapshot_lxc", ReadOnly = false, Destructive = false)]
    [Description("Creates a snapshot of an LXC container. Waits for the Proxmox task to finish and reports its real outcome. Asks the operator afresh each time and is never remembered.")]
    public Task<string> SnapshotLxc([Description(SessionDescription)] string session, [Description("The node the LXC container runs on.")] string node, [Description("The LXC container id.")] string vmid, [Description("The snapshot's name.")] string name, [Description("Optional snapshot description.")] string? description = null, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _MutateAsync(session, server, ProxmoxActionText.SnapshotLxc(node, vmid, name), selected => selected.SnapshotLxcAsync(node, vmid, name, description, cancellationToken));

    [McpServerTool(Name = "rollback_vm_snapshot", ReadOnly = false, Destructive = true)]
    [Description("Rolls a VM back to a snapshot, destroying everything that happened since it was taken. Off unless the operator turned rollback on for the selected server; when on, always asks afresh and is never remembered. Waits for the Proxmox task to finish and reports its real outcome.")]
    public Task<string> RollbackVmSnapshot([Description(SessionDescription)] string session, [Description("The node the VM runs on.")] string node, [Description("The VM id.")] string vmid, [Description("The snapshot's name to roll back to.")] string name, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _DangerAsync(session, server, DangerCapability.Rollback, target => target.AllowRollback, ProxmoxActionText.RollbackVm(node, vmid, name), selected => selected.RollbackVmSnapshotAsync(node, vmid, name, cancellationToken));

    [McpServerTool(Name = "rollback_lxc_snapshot", ReadOnly = false, Destructive = true)]
    [Description("Rolls an LXC container back to a snapshot, destroying everything that happened since it was taken. Off unless the operator turned rollback on for the selected server; when on, always asks afresh and is never remembered. Waits for the Proxmox task to finish and reports its real outcome.")]
    public Task<string> RollbackLxcSnapshot([Description(SessionDescription)] string session, [Description("The node the LXC container runs on.")] string node, [Description("The LXC container id.")] string vmid, [Description("The snapshot's name to roll back to.")] string name, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _DangerAsync(session, server, DangerCapability.Rollback, target => target.AllowRollback, ProxmoxActionText.RollbackLxc(node, vmid, name), selected => selected.RollbackLxcSnapshotAsync(node, vmid, name, cancellationToken));

    [McpServerTool(Name = "delete_vm", ReadOnly = false, Destructive = true)]
    [Description("Deletes a VM outright — irreversible. Off unless the operator turned delete on for the selected server; when on, always asks afresh and is never remembered. Waits for the Proxmox task to finish and reports its real outcome.")]
    public Task<string> DeleteVm([Description(SessionDescription)] string session, [Description("The node the VM runs on.")] string node, [Description("The VM id.")] string vmid, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _DangerAsync(session, server, DangerCapability.Delete, target => target.AllowDelete, ProxmoxActionText.DeleteVm(node, vmid), selected => selected.DeleteVmAsync(node, vmid, cancellationToken));

    [McpServerTool(Name = "delete_lxc", ReadOnly = false, Destructive = true)]
    [Description("Deletes an LXC container outright — irreversible. Off unless the operator turned delete on for the selected server; when on, always asks afresh and is never remembered. Waits for the Proxmox task to finish and reports its real outcome.")]
    public Task<string> DeleteLxc([Description(SessionDescription)] string session, [Description("The node the LXC container runs on.")] string node, [Description("The LXC container id.")] string vmid, [Description(ServerDescription)] string? server = null, CancellationToken cancellationToken = default) =>
        _DangerAsync(session, server, DangerCapability.Delete, target => target.AllowDelete, ProxmoxActionText.DeleteLxc(node, vmid), selected => selected.DeleteLxcAsync(node, vmid, cancellationToken));

    private Task<string> _PbsReadAsync(string session, string? server, string operation, Func<IProxmoxEngine, Task<object>> read) => _ReadAsync(session, server, operation, read, ProxmoxServerKind.Pbs);

    private Task<string> _PbsMutateAsync(string session, string? server, string operation, Func<IProxmoxEngine, Task<ProxmoxTaskOutcome>> action) => _MutateAsync(session, server, operation, action, ProxmoxServerKind.Pbs);

    private async Task<string> _ReadAsync(string session, string? server, string operation, Func<IProxmoxEngine, Task<object>> read, ProxmoxServerKind requiredKind = ProxmoxServerKind.Pve)
    {
        var target = _Target(server, out var error);
        if (target is null)
        {
            return error!;
        }

        if (target.Kind != requiredKind)
        {
            return McpText.Error($"This tool requires a {requiredKind.ToString().ToUpperInvariant()} server; \"{target.Name}\" is {target.Kind.ToString().ToUpperInvariant()}.");
        }

        var decision = await gate.AuthorizeConnectionAsync($"{operation} on Proxmox server \"{target.Name}\"", session, target.Id);
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

    private async Task<string> _MutateAsync(string session, string? server, string operation, Func<IProxmoxEngine, Task<ProxmoxTaskOutcome>> action, ProxmoxServerKind requiredKind = ProxmoxServerKind.Pve)
    {
        var target = _Target(server, out var error);
        if (target is null)
        {
            return error!;
        }

        if (target.Kind != requiredKind)
        {
            return McpText.Error($"This tool requires a {requiredKind.ToString().ToUpperInvariant()} server; \"{target.Name}\" is {target.Kind.ToString().ToUpperInvariant()}.");
        }

        var decision = await gate.AuthorizeMutationAsync($"{operation} on Proxmox server \"{target.Name}\"", session, target.Id);
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

        if (target.Kind != ProxmoxServerKind.Pve)
        {
            return McpText.Error($"This tool requires a PVE server; \"{target.Name}\" is {target.Kind.ToString().ToUpperInvariant()}.");
        }

        var decision = await gate.AuthorizeDangerAsync(capability, enabled(target), $"{operation} on Proxmox server \"{target.Name}\"", session, target.Id);
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
