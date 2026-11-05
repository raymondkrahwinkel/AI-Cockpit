using System.Text.Json.Nodes;
using System.Text.Json;
using Cockpit.Plugin.Proxmox.Engine;
using Cockpit.Plugin.Proxmox.Mcp;
using Cockpit.Plugin.Proxmox.Security;
using Cockpit.Plugin.Proxmox.Settings;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;

namespace Cockpit.Plugin.Proxmox.Tests;

public sealed class ProxmoxMcpToolsTests
{
    private const string Session = "pane-1";

    private sealed record Harness(ProxmoxMcpTools Tools, List<ConsentRequest> Asked, FakeProxmoxEngine Engine);

    private static Harness _Build(ConsentOutcome outcome, bool allowDelete = false)
    {
        var settings = new ProxmoxSettings(new FakePluginStorage());
        var server = new ProxmoxServer("test", "test", "test.example", 8006, "user@pam!agent", string.Empty, false, allowDelete);
        settings.SaveServers([server], new Dictionary<string, string> { [server.Id] = "test-token" });
        var asked = new List<ConsentRequest>();
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add)).Returns(new ConsentDecision(outcome));
        var gate = new ProxmoxAccessGate(host);
        var engine = new FakeProxmoxEngine();
        return new Harness(new ProxmoxMcpTools(settings, gate, engine), asked, engine);
    }

    // ---- Consent boundary --------------------------------------------------------------------------------------

    [Fact]
    public async Task StartVm_WhenDeclined_ReturnsError_AndNeverTouchesTheEngine()
    {
        var h = _Build(ConsentOutcome.Denied);
        h.Engine.Throw = new InvalidOperationException("the engine must not be called");

        var json = JsonNode.Parse(await h.Tools.StartVm(Session, "pve1", "100"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("did not approve", json["error"]!.GetValue<string>());
    }

    // ---- Off-by-default capabilities ---------------------------------------------------------------------------

    [Fact]
    public async Task DeleteVm_RespectsCapabilityAndConsent()
    {
        var disabled = _Build(ConsentOutcome.Approved, allowDelete: false);
        disabled.Engine.Throw = new InvalidOperationException("the engine must not be called");

        var blocked = JsonNode.Parse(await disabled.Tools.DeleteVm(Session, "pve1", "100"));

        Assert.False(blocked!["ok"]!.GetValue<bool>());
        Assert.Contains("settings", blocked["error"]!.GetValue<string>());
        Assert.Empty(disabled.Asked);

        var enabled = _Build(ConsentOutcome.Approved, allowDelete: true);
        enabled.Engine.NextOutcome = new ProxmoxTaskOutcome("UPID:pve1:...", IsSuccess: true, ExitStatus: "OK", TimedOut: false);

        var completed = JsonNode.Parse(await enabled.Tools.DeleteVm(Session, "pve1", "100"));

        Assert.True(completed!["ok"]!.GetValue<bool>());
        Assert.NotEmpty(enabled.Asked);
        Assert.Equal(ConsentRisk.Dangerous, enabled.Asked[^1].Risk);
    }

    [Theory]
    [InlineData("{\"datastores\":[{\"store\":\"vault\",\"total\":1099511627776,\"used\":274877906944,\"gc-status\":\"OK\",\"verify-status\":\"ok\"}],\"snapshots\":[{\"backup-type\":\"vm\",\"backup-id\":\"101\",\"backup-time\":1735689600,\"size\":42,\"verification\":\"ok\",\"protected\":true}],\"tasks\":[{\"upid\":\"UPID:pbs:1\",\"worker_type\":\"verify\",\"status\":\"OK\",\"user\":\"root@pam\",\"starttime\":1735689600,\"endtime\":1735689660}]}")]
    public void Settings_LegacySingleServer_MigratesWithoutLosingConnectionData_AndPbsRecordedResponsesParse(string recorded)
    {
        var storage = new FakePluginStorage();
        storage.Set("host", "pve.example.test");
        storage.Set("port", 9443);
        storage.Set("tokenId", "user@pam!agent");
        storage.Set("apiToken", "test-token");
        storage.Set("trustedCertFingerprint", "fingerprint");

        var server = new ProxmoxSettings(storage).Servers.Single();

        Assert.Equal("pve.example.test", server.Name);
        Assert.Equal("pve.example.test", server.Host);
        Assert.Equal(9443, server.Port);
        Assert.Equal("user@pam!agent", server.TokenId);
        Assert.Equal("fingerprint", server.TrustedCertFingerprint);
        Assert.Equal("test-token", new ProxmoxSettings(storage).GetApiToken(server.Id));

        using var document = JsonDocument.Parse(recorded);
        var datastores = PbsResponseParser.Datastores(document.RootElement.GetProperty("datastores"));
        var backups = PbsResponseParser.Backups("vault", document.RootElement.GetProperty("snapshots"));
        var tasks = PbsResponseParser.Tasks(document.RootElement.GetProperty("tasks"));

        Assert.Equal("vault", datastores.Single().Name);
        Assert.Equal("101", backups.Single().Id);
        Assert.Equal("verify", tasks.Single().Type);
    }

}
