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

    private sealed record Harness(ProxmoxMcpTools Tools, List<ConsentRequest> Asked, FakeProxmoxEngine Engine, ProxmoxSettings Settings);

    private static Harness _Build(ConsentOutcome outcome, bool allowRollback = false, bool allowDelete = false)
    {
        var settings = new ProxmoxSettings(new FakePluginStorage()) { AllowRollback = allowRollback, AllowDelete = allowDelete };
        var asked = new List<ConsentRequest>();
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add)).Returns(new ConsentDecision(outcome));
        var gate = new ProxmoxAccessGate(host);
        var engine = new FakeProxmoxEngine();
        return new Harness(new ProxmoxMcpTools(settings, gate, engine), asked, engine, settings);
    }

    // ---- The async task model: a write reports the task's real outcome, never just "accepted" ---------------------

    [Fact]
    public async Task StartVm_WhenTheTaskSucceeds_ReportsSuccessWithExitStatus()
    {
        var h = _Build(ConsentOutcome.Approved);
        h.Engine.NextOutcome = new ProxmoxTaskOutcome("UPID:pve1:...", IsSuccess: true, ExitStatus: "OK", TimedOut: false);

        var json = JsonNode.Parse(await h.Tools.StartVm(Session, "pve1", "100"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal("OK", json["exitStatus"]!.GetValue<string>());
    }

    [Fact]
    public async Task StartVm_WhenTheTaskFails_ReportsFailure_NotAcceptance()
    {
        var h = _Build(ConsentOutcome.Approved);
        h.Engine.NextOutcome = new ProxmoxTaskOutcome("UPID:pve1:...", IsSuccess: false, ExitStatus: "ipcc_send_rec failed", TimedOut: false);

        var json = JsonNode.Parse(await h.Tools.StartVm(Session, "pve1", "100"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("did not succeed", json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task StartVm_WhenTheTaskTimesOut_ReportsStillRunning_NeverSilentSuccess()
    {
        var h = _Build(ConsentOutcome.Approved);
        h.Engine.NextOutcome = new ProxmoxTaskOutcome("UPID:pve1:...", IsSuccess: false, ExitStatus: "still running", TimedOut: true);

        var json = JsonNode.Parse(await h.Tools.StartVm(Session, "pve1", "100"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("still running", json["error"]!.GetValue<string>());
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

    // ---- Shutdown vs stop: two distinct actions, never merged --------------------------------------------------

    [Fact]
    public async Task ShutdownVm_AsksAGracefulAction_DistinctFromStopVm()
    {
        var h = _Build(ConsentOutcome.Approved);

        await h.Tools.ShutdownVm(Session, "pve1", "100");
        await h.Tools.StopVm(Session, "pve1", "100");

        var shutdownAction = h.Asked[1].Action;
        var stopAction = h.Asked[3].Action;
        Assert.Contains("gracefully shut down", shutdownAction, StringComparison.Ordinal);
        Assert.Contains("hard power off", stopAction, StringComparison.Ordinal);
        Assert.NotEqual(shutdownAction, stopAction);
    }

    // ---- Off-by-default capabilities ---------------------------------------------------------------------------

    [Fact]
    public async Task DeleteVm_WhenOff_IsBlockedWithoutPrompting_AndNeverTouchesTheEngine()
    {
        var h = _Build(ConsentOutcome.Approved, allowDelete: false);
        h.Engine.Throw = new InvalidOperationException("the engine must not be called");

        var json = JsonNode.Parse(await h.Tools.DeleteVm(Session, "pve1", "100"));

        Assert.False(json!["ok"]!.GetValue<bool>());
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

    // ---- LXC naming: never "container" bare, so it cannot be confused with a Docker container --------------------

    [Fact]
    public async Task StartLxc_NamesItLxcContainer_NotBareContainer()
    {
        var h = _Build(ConsentOutcome.Approved);

        await h.Tools.StartLxc(Session, "pve1", "200");

        Assert.Contains("LXC container", h.Asked[1].Action, StringComparison.Ordinal);
    }
}
