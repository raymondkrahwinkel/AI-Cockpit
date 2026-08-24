using Cockpit.Plugin.Proxmox.Security;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;

namespace Cockpit.Plugin.Proxmox.Tests;

public sealed class ProxmoxAccessGateTests
{
    private const string Session = "pane-1";

    private static (ProxmoxAccessGate gate, List<ConsentRequest> asked) _Gate(ConsentOutcome outcome)
    {
        var asked = new List<ConsentRequest>();
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add)).Returns(new ConsentDecision(outcome));
        return (new ProxmoxAccessGate(host), asked);
    }

    [Fact]
    public async Task Connection_AsksOnce_LowRisk_RememberedForThePane()
    {
        var (gate, asked) = _Gate(ConsentOutcome.Approved);

        var result = await gate.AuthorizeConnectionAsync("list nodes", Session);

        Assert.True(result.IsAllowed);
        Assert.Single(asked);
        Assert.Equal(ConsentRisk.LowRisk, asked[0].Risk);
        Assert.True(asked[0].AllowRemember);
        Assert.Equal("proxmox.connect:default", asked[0].Scope);
        Assert.Equal(Session, asked[0].Source.PaneId);
    }

    [Fact]
    public async Task Mutation_AsksConnectionThenAlwaysDangerous_NeverRemembered()
    {
        var (gate, asked) = _Gate(ConsentOutcome.Approved);

        var result = await gate.AuthorizeMutationAsync("start VM 100 on node \"pve1\"", Session);

        Assert.True(result.IsAllowed);
        Assert.Equal(2, asked.Count);
        Assert.Equal(ConsentRisk.LowRisk, asked[0].Risk);
        Assert.Equal(ConsentRisk.Dangerous, asked[1].Risk);
        Assert.False(asked[1].AllowRemember);
        Assert.Equal("proxmox.mutate:default", asked[1].Scope);
    }

    [Fact]
    public async Task Danger_WhenCapabilityOff_IsBlockedWithASettingsHint_NoPrompt()
    {
        var (gate, asked) = _Gate(ConsentOutcome.Approved);

        var result = await gate.AuthorizeDangerAsync(DangerCapability.Delete, enabled: false, "delete VM 100", Session);

        Assert.False(result.IsAllowed);
        Assert.Contains("settings", result.DeniedReason);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task Danger_WhenCapabilityOn_AsksConnectionThenDangerous()
    {
        var (gate, asked) = _Gate(ConsentOutcome.Approved);

        var result = await gate.AuthorizeDangerAsync(DangerCapability.Rollback, enabled: true, "roll back VM 100 to snapshot \"pre-upgrade\"", Session);

        Assert.True(result.IsAllowed);
        Assert.Equal(2, asked.Count);
        Assert.Equal(ConsentRisk.Dangerous, asked[1].Risk);
        Assert.False(asked[1].AllowRemember);
        Assert.Equal("proxmox.rollback:default", asked[1].Scope);
    }

    [Fact]
    public async Task Action_IsFlattenedToASingleLine_SoAnAgentCannotSmuggleExtraLines()
    {
        var (gate, asked) = _Gate(ConsentOutcome.Approved);

        await gate.AuthorizeMutationAsync("delete\nVM\n100", Session);

        // Newlines are escaped VISIBLY (as the two literal chars \n) so the operator sees the command is multi-line —
        // an agent cannot disguise a second line as commented-out — while the consent body stays one physical line.
        Assert.Equal("delete\\nVM\\n100", asked[1].Action);
        Assert.DoesNotContain("\n", asked[1].Action);
    }

    // AC-1062, criterion 3 (mirrors ClusterAccessGate): the multi-line ingress still holds the AC-92 invariant — a
    // detail line carrying a raw newline of its own comes out escaped on its own line, not as a second line.
    [Fact]
    public async Task Mutation_ADetailLineWithAnEmbeddedNewline_ComesOutAsOneEscapedLine_NotTwoLines()
    {
        var (gate, asked) = _Gate(ConsentOutcome.Approved);

        await gate.AuthorizeMutationAsync("start VM 100 on node \"pve1\"", Session, detailLines: ["snapshot note: pre-upgrade\nrm -rf /data"]);

        Assert.Equal(2, asked[1].Action.Split('\n').Length);
        Assert.Contains("pre-upgrade\\nrm -rf /data", asked[1].Action, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenOperatorDeclines_ReturnsDenyWithReason()
    {
        var (gate, _) = _Gate(ConsentOutcome.Denied);

        var result = await gate.AuthorizeConnectionAsync("list nodes", Session);

        Assert.False(result.IsAllowed);
        Assert.Contains("did not approve", result.DeniedReason);
    }
}
