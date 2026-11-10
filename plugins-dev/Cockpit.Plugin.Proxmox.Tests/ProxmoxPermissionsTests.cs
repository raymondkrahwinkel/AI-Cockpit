using System.Text.Json;
using Cockpit.Plugin.Proxmox.Engine;

namespace Cockpit.Plugin.Proxmox.Tests;

public sealed class ProxmoxPermissionsTests
{
    // AC-2260: `{}` was measured read-only with pvesh for `root@pam!cockpit` (privsep 1, no ACL, 2026-11-10); the
    // other two follow the documented shape and live in this one test because the count gate counts every row.
    [Fact]
    public void HasPrivilege_ReadsTheEffectivePermissionsAnswer_AndTreatsTheMeasuredEmptyOneAsNoAccess()
    {
        using var measuredEmpty = JsonDocument.Parse("{}");
        using var auditor = JsonDocument.Parse("""{"/":{"Datastore.Audit":1,"Sys.Audit":1,"VM.Audit":1},"/storage/local":{"Datastore.Audit":1}}""");
        using var other = JsonDocument.Parse("""{"/vms/100":{"VM.PowerMgmt":1,"VM.Audit":0}}""");

        Assert.False(ProxmoxEngine.HasPrivilege(measuredEmpty.RootElement, "VM.Audit"));
        Assert.True(ProxmoxEngine.HasPrivilege(auditor.RootElement, "VM.Audit"));
        Assert.False(ProxmoxEngine.HasPrivilege(other.RootElement, "VM.Audit"));
    }
}
