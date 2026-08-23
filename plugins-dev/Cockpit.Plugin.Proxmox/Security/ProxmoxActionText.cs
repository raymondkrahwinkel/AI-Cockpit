namespace Cockpit.Plugin.Proxmox.Security;

// The literal consent text for each mutation (AC-1038), shared by the MCP tools and the overview workspace's
// action buttons so the two surfaces can never drift into describing the same call differently.
internal static class ProxmoxActionText
{
    public static string StartVm(string node, string vmid) => $"start VM {vmid} on node \"{node}\"";

    public static string ShutdownVm(string node, string vmid) => $"gracefully shut down VM {vmid} on node \"{node}\" (ACPI shutdown)";

    public static string StopVm(string node, string vmid) => $"hard power off VM {vmid} on node \"{node}\" (immediate, may cause data loss)";

    public static string RebootVm(string node, string vmid) => $"reboot VM {vmid} on node \"{node}\" (ACPI)";

    public static string SnapshotVm(string node, string vmid, string name) => $"create snapshot \"{name}\" of VM {vmid} on node \"{node}\"";

    public static string RollbackVm(string node, string vmid, string name) =>
        $"roll back VM {vmid} on node \"{node}\" to snapshot \"{name}\" — this destroys everything since that snapshot";

    public static string DeleteVm(string node, string vmid) => $"delete VM {vmid} on node \"{node}\" — irreversible";

    public static string StartLxc(string node, string vmid) => $"start LXC container {vmid} on node \"{node}\"";

    public static string ShutdownLxc(string node, string vmid) => $"gracefully shut down LXC container {vmid} on node \"{node}\"";

    public static string StopLxc(string node, string vmid) => $"hard stop LXC container {vmid} on node \"{node}\" (immediate, may cause data loss)";

    public static string RebootLxc(string node, string vmid) => $"reboot LXC container {vmid} on node \"{node}\"";

    public static string SnapshotLxc(string node, string vmid, string name) => $"create snapshot \"{name}\" of LXC container {vmid} on node \"{node}\"";

    public static string RollbackLxc(string node, string vmid, string name) =>
        $"roll back LXC container {vmid} on node \"{node}\" to snapshot \"{name}\" — this destroys everything since that snapshot";

    public static string DeleteLxc(string node, string vmid) => $"delete LXC container {vmid} on node \"{node}\" — irreversible";
}
