using System.ComponentModel;
using ModelContextProtocol.Server;
using Cockpit.Plugin.Proxmox.Engine;
using Cockpit.Plugin.Proxmox.Security;
using Cockpit.Plugin.Proxmox.Settings;

namespace Cockpit.Plugin.Proxmox.Mcp;

// The `cockpit-proxmox` MCP tool surface (AC-1038). Every method routes through `ProxmoxAccessGate` first, and
// every write waits for the underlying Proxmox task (a UPID) to report its real outcome, never just acceptance.
// LXC tools always say "lxc" rather than "container", so they read distinctly from a Docker container.
internal sealed class ProxmoxMcpTools(ProxmoxSettings settings, ProxmoxAccessGate gate, IProxmoxEngine engine)
{
    // ---- Reads -------------------------------------------------------------------------------------------------

    [McpServerTool(Name = "version")]
    [Description("Returns the Proxmox VE API's version and release. This is the first call that touches the API, so it asks the operator for consent once; after that, reads are free for the session. Start here to confirm the target is reachable.")]
    public async Task<string> Version(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeConnectionAsync("read the Proxmox API version", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var version = await engine.GetVersionAsync(cancellationToken);
            return McpText.Ok(new { ok = true, release = version.Release, repoId = version.RepoId, version = version.Version });
        }
        catch (Exception ex)
        {
            return _Failure(ex);
        }
    }

    [McpServerTool(Name = "list_nodes")]
    [Description("Lists the nodes in this Proxmox target with their status, CPU usage and memory usage. A read behind the one-time connection consent.")]
    public async Task<string> ListNodes(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeConnectionAsync("list nodes", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var nodes = await engine.ListNodesAsync(cancellationToken);
            return McpText.Ok(new
            {
                ok = true,
                count = nodes.Count,
                nodes = nodes.Select(node => new
                {
                    node = node.Node,
                    status = node.Status,
                    cpuUsagePercent = node.CpuUsage,
                    maxCpu = node.MaxCpu,
                    memUsedBytes = node.MemUsed,
                    memMaxBytes = node.MemMax,
                    uptimeSeconds = node.Uptime,
                }),
            });
        }
        catch (Exception ex)
        {
            return _Failure(ex);
        }
    }

    [McpServerTool(Name = "cluster_status")]
    [Description("Reports whether this target is a Proxmox cluster and, if so, its name, quorum state and node count. A single host reports isCluster=false. A read behind the one-time connection consent.")]
    public async Task<string> ClusterStatus(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeConnectionAsync("read cluster status", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var info = await engine.GetClusterInfoAsync(cancellationToken);
            return McpText.Ok(new { ok = true, isCluster = info.IsCluster, name = info.Name, quorate = info.Quorate, nodeCount = info.NodeCount });
        }
        catch (Exception ex)
        {
            return _Failure(ex);
        }
    }

    [McpServerTool(Name = "list_vms")]
    [Description("Lists every QEMU VM across the target (single host or cluster alike): id, name, node, status, assigned memory/disk/CPU and uptime. A read behind the one-time connection consent.")]
    public async Task<string> ListVms(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeConnectionAsync("list VMs", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var vms = await engine.ListVmsAsync(cancellationToken);
            return McpText.Ok(new { ok = true, count = vms.Count, vms = vms.Select(_GuestPayload) });
        }
        catch (Exception ex)
        {
            return _Failure(ex);
        }
    }

    [McpServerTool(Name = "list_lxc")]
    [Description("Lists every LXC container across the target (single host or cluster alike): id, name, node, status, assigned memory/disk/CPU and uptime. These are Proxmox LXC containers, distinct from Docker containers. A read behind the one-time connection consent.")]
    public async Task<string> ListLxc(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeConnectionAsync("list LXC containers", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var lxc = await engine.ListLxcAsync(cancellationToken);
            return McpText.Ok(new { ok = true, count = lxc.Count, lxc = lxc.Select(_GuestPayload) });
        }
        catch (Exception ex)
        {
            return _Failure(ex);
        }
    }

    [McpServerTool(Name = "list_storage")]
    [Description("Lists storage pools across the target with total and used bytes. A read behind the one-time connection consent.")]
    public async Task<string> ListStorage(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeConnectionAsync("list storage pools", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var pools = await engine.ListStorageAsync(cancellationToken);
            return McpText.Ok(new
            {
                ok = true,
                count = pools.Count,
                storage = pools.Select(pool => new
                {
                    storage = pool.Storage,
                    node = pool.Node,
                    type = pool.Type,
                    totalBytes = pool.TotalBytes,
                    usedBytes = pool.UsedBytes,
                    enabled = pool.Enabled,
                }),
            });
        }
        catch (Exception ex)
        {
            return _Failure(ex);
        }
    }

    [McpServerTool(Name = "list_tasks")]
    [Description("Lists a node's recent tasks — what ran or is running, with its outcome. Use this to follow up on a task that timed out while waiting. A read behind the one-time connection consent.")]
    public async Task<string> ListTasks(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The node name, as returned by list_nodes.")] string node,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeConnectionAsync($"list tasks on node \"{node}\"", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var tasks = await engine.ListTasksAsync(node, cancellationToken);
            return McpText.Ok(new
            {
                ok = true,
                count = tasks.Count,
                tasks = tasks.Select(task => new { upid = task.Upid, type = task.Type, status = task.Status, user = task.User, startTime = task.StartTime, endTime = task.EndTime }),
            });
        }
        catch (Exception ex)
        {
            return _Failure(ex);
        }
    }

    [McpServerTool(Name = "list_vm_snapshots")]
    [Description("Lists a VM's snapshots: name, description and creation time. A read behind the one-time connection consent.")]
    public Task<string> ListVmSnapshots(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The node the VM runs on.")] string node,
        [Description("The VM id.")] string vmid,
        CancellationToken cancellationToken = default) =>
        _ListSnapshotsAsync(session, node, vmid, isLxc: false, cancellationToken);

    [McpServerTool(Name = "list_lxc_snapshots")]
    [Description("Lists an LXC container's snapshots: name, description and creation time. A read behind the one-time connection consent.")]
    public Task<string> ListLxcSnapshots(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The node the LXC container runs on.")] string node,
        [Description("The LXC container id.")] string vmid,
        CancellationToken cancellationToken = default) =>
        _ListSnapshotsAsync(session, node, vmid, isLxc: true, cancellationToken);

    // ---- VM mutations (always Dangerous, never remembered) ------------------------------------------------------

    [McpServerTool(Name = "start_vm")]
    [Description("Starts a stopped VM. Waits for the Proxmox task to finish and reports its real outcome. This is a change, so it asks the operator afresh each time and is never remembered.")]
    public Task<string> StartVm(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The node the VM runs on.")] string node,
        [Description("The VM id.")] string vmid,
        CancellationToken cancellationToken = default) =>
        _MutateAsync(ProxmoxActionText.StartVm(node, vmid), session, ct => engine.StartVmAsync(node, vmid, ct), cancellationToken);

    [McpServerTool(Name = "shutdown_vm")]
    [Description("Gracefully shuts a VM down (an ACPI shutdown request the guest OS can act on) — distinct from stop_vm, which powers it off immediately. Waits for the Proxmox task to finish and reports its real outcome. Asks the operator afresh each time and is never remembered.")]
    public Task<string> ShutdownVm(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The node the VM runs on.")] string node,
        [Description("The VM id.")] string vmid,
        CancellationToken cancellationToken = default) =>
        _MutateAsync(ProxmoxActionText.ShutdownVm(node, vmid), session, ct => engine.ShutdownVmAsync(node, vmid, ct), cancellationToken);

    [McpServerTool(Name = "stop_vm")]
    [Description("Hard-powers a VM off immediately, with no guest cooperation — distinct from shutdown_vm's graceful ACPI request. Like pulling the power cord: unsaved guest state can be lost. Waits for the Proxmox task to finish and reports its real outcome. Asks the operator afresh each time and is never remembered.")]
    public Task<string> StopVm(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The node the VM runs on.")] string node,
        [Description("The VM id.")] string vmid,
        CancellationToken cancellationToken = default) =>
        _MutateAsync(ProxmoxActionText.StopVm(node, vmid), session, ct => engine.StopVmAsync(node, vmid, ct), cancellationToken);

    [McpServerTool(Name = "reboot_vm")]
    [Description("Gracefully reboots a VM (ACPI). Waits for the Proxmox task to finish and reports its real outcome. Asks the operator afresh each time and is never remembered.")]
    public Task<string> RebootVm(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The node the VM runs on.")] string node,
        [Description("The VM id.")] string vmid,
        CancellationToken cancellationToken = default) =>
        _MutateAsync(ProxmoxActionText.RebootVm(node, vmid), session, ct => engine.RebootVmAsync(node, vmid, ct), cancellationToken);

    [McpServerTool(Name = "snapshot_vm")]
    [Description("Creates a snapshot of a VM. Waits for the Proxmox task to finish and reports its real outcome. Asks the operator afresh each time and is never remembered.")]
    public Task<string> SnapshotVm(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The node the VM runs on.")] string node,
        [Description("The VM id.")] string vmid,
        [Description("The snapshot's name.")] string name,
        [Description("Optional snapshot description.")] string? description = null,
        CancellationToken cancellationToken = default) =>
        _MutateAsync(ProxmoxActionText.SnapshotVm(node, vmid, name), session, ct => engine.SnapshotVmAsync(node, vmid, name, description, ct), cancellationToken);

    // ---- LXC mutations (always Dangerous, never remembered) ------------------------------------------------------

    [McpServerTool(Name = "start_lxc")]
    [Description("Starts a stopped LXC container. Waits for the Proxmox task to finish and reports its real outcome. Asks the operator afresh each time and is never remembered.")]
    public Task<string> StartLxc(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The node the LXC container runs on.")] string node,
        [Description("The LXC container id.")] string vmid,
        CancellationToken cancellationToken = default) =>
        _MutateAsync(ProxmoxActionText.StartLxc(node, vmid), session, ct => engine.StartLxcAsync(node, vmid, ct), cancellationToken);

    [McpServerTool(Name = "shutdown_lxc")]
    [Description("Gracefully shuts an LXC container down — distinct from stop_lxc, which stops it immediately. Waits for the Proxmox task to finish and reports its real outcome. Asks the operator afresh each time and is never remembered.")]
    public Task<string> ShutdownLxc(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The node the LXC container runs on.")] string node,
        [Description("The LXC container id.")] string vmid,
        CancellationToken cancellationToken = default) =>
        _MutateAsync(ProxmoxActionText.ShutdownLxc(node, vmid), session, ct => engine.ShutdownLxcAsync(node, vmid, ct), cancellationToken);

    [McpServerTool(Name = "stop_lxc")]
    [Description("Stops an LXC container immediately, with no cooperation from the processes inside it — distinct from shutdown_lxc's graceful stop. Waits for the Proxmox task to finish and reports its real outcome. Asks the operator afresh each time and is never remembered.")]
    public Task<string> StopLxc(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The node the LXC container runs on.")] string node,
        [Description("The LXC container id.")] string vmid,
        CancellationToken cancellationToken = default) =>
        _MutateAsync(ProxmoxActionText.StopLxc(node, vmid), session, ct => engine.StopLxcAsync(node, vmid, ct), cancellationToken);

    [McpServerTool(Name = "reboot_lxc")]
    [Description("Reboots an LXC container. Waits for the Proxmox task to finish and reports its real outcome. Asks the operator afresh each time and is never remembered.")]
    public Task<string> RebootLxc(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The node the LXC container runs on.")] string node,
        [Description("The LXC container id.")] string vmid,
        CancellationToken cancellationToken = default) =>
        _MutateAsync(ProxmoxActionText.RebootLxc(node, vmid), session, ct => engine.RebootLxcAsync(node, vmid, ct), cancellationToken);

    [McpServerTool(Name = "snapshot_lxc")]
    [Description("Creates a snapshot of an LXC container. Waits for the Proxmox task to finish and reports its real outcome. Asks the operator afresh each time and is never remembered.")]
    public Task<string> SnapshotLxc(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The node the LXC container runs on.")] string node,
        [Description("The LXC container id.")] string vmid,
        [Description("The snapshot's name.")] string name,
        [Description("Optional snapshot description.")] string? description = null,
        CancellationToken cancellationToken = default) =>
        _MutateAsync(ProxmoxActionText.SnapshotLxc(node, vmid, name), session, ct => engine.SnapshotLxcAsync(node, vmid, name, description, ct), cancellationToken);

    // ---- Rollback / delete (off by default, per-capability) -------------------------------------------------------

    [McpServerTool(Name = "rollback_vm_snapshot")]
    [Description("Rolls a VM back to a snapshot, destroying everything that happened since it was taken. Off unless the operator turned rollback on in the plugin settings; when on, always asks afresh and is never remembered. Waits for the Proxmox task to finish and reports its real outcome.")]
    public async Task<string> RollbackVmSnapshot(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The node the VM runs on.")] string node,
        [Description("The VM id.")] string vmid,
        [Description("The snapshot's name to roll back to.")] string name,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeDangerAsync(
            DangerCapability.Rollback, settings.AllowRollback, ProxmoxActionText.RollbackVm(node, vmid, name), session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var outcome = await engine.RollbackVmSnapshotAsync(node, vmid, name, cancellationToken);
            return _TaskResult(outcome);
        }
        catch (Exception ex)
        {
            return _Failure(ex);
        }
    }

    [McpServerTool(Name = "rollback_lxc_snapshot")]
    [Description("Rolls an LXC container back to a snapshot, destroying everything that happened since it was taken. Off unless the operator turned rollback on in the plugin settings; when on, always asks afresh and is never remembered. Waits for the Proxmox task to finish and reports its real outcome.")]
    public async Task<string> RollbackLxcSnapshot(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The node the LXC container runs on.")] string node,
        [Description("The LXC container id.")] string vmid,
        [Description("The snapshot's name to roll back to.")] string name,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeDangerAsync(
            DangerCapability.Rollback, settings.AllowRollback, ProxmoxActionText.RollbackLxc(node, vmid, name), session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var outcome = await engine.RollbackLxcSnapshotAsync(node, vmid, name, cancellationToken);
            return _TaskResult(outcome);
        }
        catch (Exception ex)
        {
            return _Failure(ex);
        }
    }

    [McpServerTool(Name = "delete_vm")]
    [Description("Deletes a VM outright — irreversible. Off unless the operator turned delete on in the plugin settings; when on, always asks afresh and is never remembered. Waits for the Proxmox task to finish and reports its real outcome.")]
    public async Task<string> DeleteVm(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The node the VM runs on.")] string node,
        [Description("The VM id.")] string vmid,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeDangerAsync(
            DangerCapability.Delete, settings.AllowDelete, ProxmoxActionText.DeleteVm(node, vmid), session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var outcome = await engine.DeleteVmAsync(node, vmid, cancellationToken);
            return _TaskResult(outcome);
        }
        catch (Exception ex)
        {
            return _Failure(ex);
        }
    }

    [McpServerTool(Name = "delete_lxc")]
    [Description("Deletes an LXC container outright — irreversible. Off unless the operator turned delete on in the plugin settings; when on, always asks afresh and is never remembered. Waits for the Proxmox task to finish and reports its real outcome.")]
    public async Task<string> DeleteLxc(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The node the LXC container runs on.")] string node,
        [Description("The LXC container id.")] string vmid,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeDangerAsync(
            DangerCapability.Delete, settings.AllowDelete, ProxmoxActionText.DeleteLxc(node, vmid), session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var outcome = await engine.DeleteLxcAsync(node, vmid, cancellationToken);
            return _TaskResult(outcome);
        }
        catch (Exception ex)
        {
            return _Failure(ex);
        }
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------

    private async Task<string> _ListSnapshotsAsync(string session, string node, string vmid, bool isLxc, CancellationToken cancellationToken)
    {
        var kind = isLxc ? "LXC container" : "VM";
        var decision = await gate.AuthorizeConnectionAsync($"list snapshots of {kind} {vmid} on node \"{node}\"", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var snapshots = isLxc
                ? await engine.ListLxcSnapshotsAsync(node, vmid, cancellationToken)
                : await engine.ListVmSnapshotsAsync(node, vmid, cancellationToken);
            return McpText.Ok(new
            {
                ok = true,
                count = snapshots.Count,
                snapshots = snapshots.Select(snapshot => new { name = snapshot.Name, description = snapshot.Description, snapTime = snapshot.SnapTime, isCurrent = snapshot.IsCurrent }),
            });
        }
        catch (Exception ex)
        {
            return _Failure(ex);
        }
    }

    private async Task<string> _MutateAsync(string operation, string session, Func<CancellationToken, Task<ProxmoxTaskOutcome>> action, CancellationToken cancellationToken)
    {
        var decision = await gate.AuthorizeMutationAsync(operation, session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        try
        {
            var outcome = await action(cancellationToken);
            return _TaskResult(outcome);
        }
        catch (Exception ex)
        {
            return _Failure(ex);
        }
    }

    private static string _TaskResult(ProxmoxTaskOutcome outcome)
    {
        if (outcome.TimedOut)
        {
            return McpText.Error($"The task (upid={outcome.Upid}) is still running and was not confirmed to finish. Check its status with list_tasks.");
        }

        return outcome.IsSuccess
            ? McpText.Ok(new { ok = true, upid = outcome.Upid, exitStatus = outcome.ExitStatus })
            : McpText.Error($"The task (upid={outcome.Upid}) did not succeed: {outcome.ExitStatus}");
    }

    private static object _GuestPayload(ProxmoxGuest guest) => new
    {
        vmid = guest.VmId,
        name = guest.Name,
        node = guest.Node,
        status = guest.Status,
        maxMemBytes = guest.MaxMem,
        maxDiskBytes = guest.MaxDisk,
        maxCpu = guest.MaxCpu,
        uptimeSeconds = guest.Uptime,
    };

    // Never leak a stack trace or raw exception text to the agent (AC-1038 criterion 6) — a `ProxmoxApiException`
    // is already a readable, safe message; anything else is reduced to its type name.
    private static string _Failure(Exception ex) => ex switch
    {
        OperationCanceledException => McpText.Error("The operation was cancelled."),
        ProxmoxApiException apiEx => McpText.Error(apiEx.Message),
        _ => McpText.Error($"The Proxmox request failed ({ex.GetType().Name})."),
    };
}
