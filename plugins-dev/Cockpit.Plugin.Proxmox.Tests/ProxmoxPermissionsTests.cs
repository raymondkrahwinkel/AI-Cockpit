using System.Text.Json;
using Cockpit.Plugin.Proxmox.Engine;

namespace Cockpit.Plugin.Proxmox.Tests;

public sealed class ProxmoxPermissionsTests
{
    // `GET /access/permissions` as Proxmox VE documents it: path -> { privilege: 1 }, and `{}` for a token that may do nothing.
    [Fact]
    public void HasPrivilege_ReadsTheEffectivePermissionsAnswer_AndTreatsAnEmptyOneAsNoAccess()
    {
        using var auditor = JsonDocument.Parse("""{"/":{"Datastore.Audit":1,"Sys.Audit":1,"VM.Audit":1},"/storage/local":{"Datastore.Audit":1}}""");
        using var none = JsonDocument.Parse("{}");
        using var other = JsonDocument.Parse("""{"/vms/100":{"VM.PowerMgmt":1,"VM.Audit":0}}""");

        Assert.True(ProxmoxEngine.HasPrivilege(auditor.RootElement, "VM.Audit"));
        Assert.False(ProxmoxEngine.HasPrivilege(none.RootElement, "VM.Audit"));
        Assert.False(ProxmoxEngine.HasPrivilege(other.RootElement, "VM.Audit"));
    }
}
