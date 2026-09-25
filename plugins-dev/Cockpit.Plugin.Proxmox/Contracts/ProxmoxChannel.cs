using System.Text.Json;

namespace Cockpit.Plugin.Proxmox.Contracts;

// AC-1394: what the backend part answers the UI part over the plugin's channel. Compiled into both assemblies as
// a linked source file rather than shared as an assembly, so the UI part never references the backend part.
internal static class ProxmoxChannel
{
    // Payload: ProxmoxEmptyRequest. Answers a ProxmoxOverviewAnswer — the connection gate's decision plus, when
    // allowed, cluster/nodes/VMs/LXC containers/storage in one round trip.
    public const string Overview = "overview";

    // Payload: ProxmoxGuestActionRequest. Answers a ProxmoxActionAnswer for starting the named VM/LXC container.
    public const string StartGuest = "start-guest";

    // Payload: ProxmoxGuestActionRequest. Answers a ProxmoxActionAnswer for gracefully shutting it down.
    public const string ShutdownGuest = "shutdown-guest";

    // Payload: ProxmoxGuestActionRequest. Answers a ProxmoxActionAnswer for hard-stopping it.
    public const string StopGuest = "stop-guest";

    // Payload: ProxmoxEmptyRequest. Drops the backend's cached API client after a settings save. Answers a
    // ProxmoxEmptyRequest back — there is nothing to report either way.
    public const string InvalidateTarget = "invalidate-target";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

internal sealed record ProxmoxEmptyRequest();

internal sealed record ProxmoxGuestActionRequest(string Node, string VmId, bool IsLxc);

// What the overview workspace shows in one round trip. DeniedReason is set instead of the rest when the connection
// gate refused; ErrorMessage instead when the Proxmox API call itself failed. The engine's own records are
// re-shaped here since the UI part cannot reference them.
internal sealed record ProxmoxOverviewAnswer(
    string? DeniedReason,
    string? ErrorMessage,
    ProxmoxClusterData? Cluster,
    IReadOnlyList<ProxmoxNodeData>? Nodes,
    IReadOnlyList<ProxmoxGuestData>? Vms,
    IReadOnlyList<ProxmoxGuestData>? Lxc,
    IReadOnlyList<ProxmoxStorageData>? Storage);

internal sealed record ProxmoxClusterData(bool IsCluster, string? Name, bool Quorate, int NodeCount);

internal sealed record ProxmoxNodeData(string Node, string Status, double CpuUsage, int MaxCpu, long MemUsed, long MemMax, long Uptime);

// A VM or LXC container summary — which list this appears in (Vms or Lxc) says which kind it is.
internal sealed record ProxmoxGuestData(string VmId, string Name, string Node, string Status, long MaxMem, long MaxDisk, double MaxCpu, long Uptime);

internal sealed record ProxmoxStorageData(string Storage, string Node, string Type, long TotalBytes, long UsedBytes, bool Enabled);

// The result of a start/shutdown/stop click. DeniedReason or ErrorMessage is set instead of Outcome when the gate
// refused or the Proxmox API call failed.
internal sealed record ProxmoxActionAnswer(string? DeniedReason, string? ErrorMessage, ProxmoxTaskOutcomeData? Outcome);

internal sealed record ProxmoxTaskOutcomeData(string Upid, bool IsSuccess, string ExitStatus, bool TimedOut);
