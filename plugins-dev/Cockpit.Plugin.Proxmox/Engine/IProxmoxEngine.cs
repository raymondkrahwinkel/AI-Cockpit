namespace Cockpit.Plugin.Proxmox.Engine;

/// <summary>
/// The plugin's own thin seam over the Proxmox VE REST API. Keeping the MCP tools behind this interface (rather
/// than touching <see cref="System.Net.Http.HttpClient"/> directly) means the tool layer — the part that carries
/// the consent gate — is testable with a fake API, mirroring the Docker plugin's <c>IDockerEngine</c>.
/// </summary>
internal interface IProxmoxEngine
{
    /// <summary>The API's version (<c>/version</c>). Touches the API — the first call that does, in an MCP session.</summary>
    Task<ProxmoxVersion> GetVersionAsync(CancellationToken cancellationToken);

    /// <summary>Lists the nodes in this target (<c>/nodes</c>) with their status and resource usage. A read.</summary>
    Task<IReadOnlyList<ProxmoxNode>> ListNodesAsync(CancellationToken cancellationToken);

    /// <summary>Whether this target is a cluster and, if so, its name and quorum state (<c>/cluster/status</c>). A read.</summary>
    Task<ProxmoxClusterInfo> GetClusterInfoAsync(CancellationToken cancellationToken);

    /// <summary>Lists every VM (QEMU) across the target (<c>/cluster/resources?type=vm</c>) — works for a single host or a cluster alike. A read.</summary>
    Task<IReadOnlyList<ProxmoxGuest>> ListVmsAsync(CancellationToken cancellationToken);

    /// <summary>Lists every LXC container across the target (<c>/cluster/resources?type=lxc</c>). A read.</summary>
    Task<IReadOnlyList<ProxmoxGuest>> ListLxcAsync(CancellationToken cancellationToken);

    /// <summary>Starts a stopped VM; waits for and reports the task's real outcome.</summary>
    Task<ProxmoxTaskOutcome> StartVmAsync(string node, string vmId, CancellationToken cancellationToken);

    /// <summary>Gracefully shuts a VM down (ACPI); waits for and reports the task's real outcome.</summary>
    Task<ProxmoxTaskOutcome> ShutdownVmAsync(string node, string vmId, CancellationToken cancellationToken);

    /// <summary>Hard-powers a VM off (immediate, no guest cooperation); waits for and reports the task's real outcome.</summary>
    Task<ProxmoxTaskOutcome> StopVmAsync(string node, string vmId, CancellationToken cancellationToken);

    /// <summary>Gracefully reboots a VM (ACPI); waits for and reports the task's real outcome.</summary>
    Task<ProxmoxTaskOutcome> RebootVmAsync(string node, string vmId, CancellationToken cancellationToken);

    /// <summary>Deletes a VM outright; waits for and reports the task's real outcome.</summary>
    Task<ProxmoxTaskOutcome> DeleteVmAsync(string node, string vmId, CancellationToken cancellationToken);

    /// <summary>Starts a stopped LXC container; waits for and reports the task's real outcome.</summary>
    Task<ProxmoxTaskOutcome> StartLxcAsync(string node, string vmId, CancellationToken cancellationToken);

    /// <summary>Gracefully shuts an LXC container down; waits for and reports the task's real outcome.</summary>
    Task<ProxmoxTaskOutcome> ShutdownLxcAsync(string node, string vmId, CancellationToken cancellationToken);

    /// <summary>Hard-stops an LXC container (immediate); waits for and reports the task's real outcome.</summary>
    Task<ProxmoxTaskOutcome> StopLxcAsync(string node, string vmId, CancellationToken cancellationToken);

    /// <summary>Reboots an LXC container; waits for and reports the task's real outcome.</summary>
    Task<ProxmoxTaskOutcome> RebootLxcAsync(string node, string vmId, CancellationToken cancellationToken);

    /// <summary>Deletes an LXC container outright; waits for and reports the task's real outcome.</summary>
    Task<ProxmoxTaskOutcome> DeleteLxcAsync(string node, string vmId, CancellationToken cancellationToken);

    /// <summary>Lists a VM's snapshots (name, description, creation time). A read.</summary>
    Task<IReadOnlyList<ProxmoxSnapshot>> ListVmSnapshotsAsync(string node, string vmId, CancellationToken cancellationToken);

    /// <summary>Lists an LXC container's snapshots. A read.</summary>
    Task<IReadOnlyList<ProxmoxSnapshot>> ListLxcSnapshotsAsync(string node, string vmId, CancellationToken cancellationToken);

    /// <summary>Creates a VM snapshot; waits for and reports the task's real outcome.</summary>
    Task<ProxmoxTaskOutcome> SnapshotVmAsync(string node, string vmId, string name, string? description, CancellationToken cancellationToken);

    /// <summary>Creates an LXC container snapshot; waits for and reports the task's real outcome.</summary>
    Task<ProxmoxTaskOutcome> SnapshotLxcAsync(string node, string vmId, string name, string? description, CancellationToken cancellationToken);

    /// <summary>Rolls a VM back to a snapshot — destructive for everything since; waits for and reports the task's real outcome.</summary>
    Task<ProxmoxTaskOutcome> RollbackVmSnapshotAsync(string node, string vmId, string name, CancellationToken cancellationToken);

    /// <summary>Rolls an LXC container back to a snapshot; waits for and reports the task's real outcome.</summary>
    Task<ProxmoxTaskOutcome> RollbackLxcSnapshotAsync(string node, string vmId, string name, CancellationToken cancellationToken);

    /// <summary>Lists storage pools across the target with how full each is (<c>/cluster/resources?type=storage</c>). A read.</summary>
    Task<IReadOnlyList<ProxmoxStoragePool>> ListStorageAsync(CancellationToken cancellationToken);

    /// <summary>Lists a node's recent tasks — what ran or is running, with outcome. A read.</summary>
    Task<IReadOnlyList<ProxmoxTaskSummary>> ListTasksAsync(string node, CancellationToken cancellationToken);
}

// The API's own version/release, from `/version`.
internal sealed record ProxmoxVersion(string Release, string RepoId, string Version);

// One node's status and resource usage, from `/nodes`.
internal sealed record ProxmoxNode(string Node, string Status, double CpuUsage, int MaxCpu, long MemUsed, long MemMax, long Uptime);

// Whether the target is a cluster and its quorum state, from `/cluster/status`.
internal sealed record ProxmoxClusterInfo(bool IsCluster, string? Name, bool Quorate, int NodeCount);

// A VM or LXC container summary from `/cluster/resources` — enough for a list, node/id/type identify it for every
// other call. `Type` is the literal Proxmox resource type ("qemu" or "lxc") so a caller can tell them apart without
// guessing from the id.
internal sealed record ProxmoxGuest(string VmId, string Name, string Node, string Type, string Status, long MaxMem, long MaxDisk, double MaxCpu, long Uptime);

// The result of waiting for an asynchronous Proxmox task (a UPID) to finish. `TimedOut` means the wait gave up
// before the task exited — the task may still be running or may since have finished; this is never reported as success.
internal sealed record ProxmoxTaskOutcome(string Upid, bool IsSuccess, string ExitStatus, bool TimedOut);

// A VM/LXC snapshot, from its `snapshot` endpoint.
internal sealed record ProxmoxSnapshot(string Name, string? Description, long? SnapTime, bool IsCurrent);

// A storage pool's capacity, from `/cluster/resources?type=storage`.
internal sealed record ProxmoxStoragePool(string Storage, string Node, string Type, long TotalBytes, long UsedBytes, bool Enabled);

// One entry from a node's task history, from `/nodes/{node}/tasks`.
internal sealed record ProxmoxTaskSummary(string Upid, string Type, string? Status, string User, long StartTime, long? EndTime);
