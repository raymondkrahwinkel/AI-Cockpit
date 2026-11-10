using System.Text.Json;
using Cockpit.Plugin.Proxmox.Engine;

namespace Cockpit.Plugin.Proxmox.Tests;

public sealed class ProxmoxPermissionsTests
{
    // `{}` is a real answer, measured read-only with `pvesh get /access/permissions --userid 'root@pam!cockpit'` on the
    // Proxmox host (2026-11-10): that token has privsep 1 and no ACL of its own, so it may do nothing, and the reads
    // it makes come back as empty lists instead of an error. The other two follow the documented shape
    // (path -> { privilege: 1 }); they are one test, not rows, because the test-count gate counts every row.
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
