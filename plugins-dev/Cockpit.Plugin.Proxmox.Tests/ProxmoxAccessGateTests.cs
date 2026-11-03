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

}
