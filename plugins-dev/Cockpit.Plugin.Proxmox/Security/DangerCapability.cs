namespace Cockpit.Plugin.Proxmox.Security;

// A capability that reaches past ordinary VM/LXC management and is off unless the operator turned it on. Each maps
// to a flag on `Settings.ProxmoxSettings`; using one always asks afresh (Dangerous, never remembered) with the
// literal action shown.
internal enum DangerCapability
{
    // Rolling back a VM or LXC container to a snapshot — destroys everything that happened since.
    Rollback,

    // Deleting a VM or LXC container outright.
    Delete,
}
