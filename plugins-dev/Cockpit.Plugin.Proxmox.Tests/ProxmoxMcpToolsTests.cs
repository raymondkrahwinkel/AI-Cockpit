using System.Text.Json.Nodes;
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
        var settings = new ProxmoxSettings(new FakePluginStorage()) { AllowDelete = allowDelete };
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
    public async Task DeleteVm_WhenOff_IsBlockedWithoutPrompting_AndNeverTouchesTheEngine()
    {
        var h = _Build(ConsentOutcome.Approved, allowDelete: false);
        h.Engine.Throw = new InvalidOperationException("the engine must not be called");

        var json = JsonNode.Parse(await h.Tools.DeleteVm(Session, "pve1", "100"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("settings", json["error"]!.GetValue<string>());
        Assert.Empty(h.Asked);
    }

    [Fact]
    public async Task DeleteVm_WhenOn_AsksEveryTime_AndReportsTheTaskOutcome()
    {
        var h = _Build(ConsentOutcome.Approved, allowDelete: true);
        h.Engine.NextOutcome = new ProxmoxTaskOutcome("UPID:pve1:...", IsSuccess: true, ExitStatus: "OK", TimedOut: false);

        var json = JsonNode.Parse(await h.Tools.DeleteVm(Session, "pve1", "100"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.NotEmpty(h.Asked);
        Assert.Equal(ConsentRisk.Dangerous, h.Asked[^1].Risk);
    }

}
