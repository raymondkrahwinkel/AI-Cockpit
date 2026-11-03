extern alias backend;

using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Infrastructure.Diagrams;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;
using DiagramMcpTools = backend::Cockpit.Plugin.Diagram.DiagramMcpTools;

namespace Cockpit.Plugin.Diagram.Tests;

// The cockpit-diagram tools (AC-810): reading a surface is gated behind its own Approve/Deny, editing behind a
// separate one, coupling is one-agent-per-surface, coupling on its own grants nothing, and a read always returns
// the surface exactly as it stands (never just what changed since the coupling — AC-810's deviation from AC-34).
[Collection("avalonia")]
public class DiagramMcpToolsTests
{
    private const string Session = "pane-agent";
    private const string Source = "flowchart LR\nA-->B";

    private static (DiagramMcpTools tools, DiagramAccessRegistry registry, ICockpitHost host, List<ConsentRequest> asked) _Build(ConsentOutcome outcome)
    {
        var registry = new DiagramAccessRegistry();
        var asked = new List<ConsentRequest>();
        var host = Substitute.For<ICockpitHost, IWindowProbe>();
        // NSubstitute defaults an unconfigured string-returning member to "", not null — leaving this unset would
        // make `host.CurrentMcpCallerPaneId ?? session` pick "" over the caller-supplied session on every test.
        host.CurrentMcpCallerPaneId.Returns((string?)null);
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add)).Returns(new ConsentDecision(outcome));
        return (new DiagramMcpTools(host, registry, new backend::Cockpit.Plugin.Diagram.DiagramSettings(new FakePluginStorage()), TestChannel.Desktop(host, diagrams: registry)), registry, host, asked);
    }

    [Fact]
    public async Task ReadDiagram_KeysOnTheVerifiedPane_NotTheAgentSuppliedSessionId()
    {
        // Hardening (AC-89 pattern), same as TerminalMcpTools: coupling is keyed on the transport-verified pane.
        var (tools, registry, host, _) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("diagram-1", "Onboarding flow", Source);
        registry.Grant("victim-pane", "diagram-1", DiagramCapability.Read);
        host.CurrentMcpCallerPaneId.Returns("attacker-pane");

        var json = JsonNode.Parse(await tools.ReadDiagram("victim-pane", "Onboarding flow"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("another agent", json["error"]!.GetValue<string>());
        Assert.Null(json["source"]);
    }

    [Fact]
    public async Task ReadDiagram_WhenDenied_ReturnsError_AndDoesNotCouple()
    {
        var (tools, registry, _, _) = _Build(ConsentOutcome.Denied);
        registry.SurfaceOpened("diagram-1", "Onboarding flow", Source);

        var json = JsonNode.Parse(await tools.ReadDiagram(Session, "Onboarding flow"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("not approved", json["error"]!.GetValue<string>());
        Assert.Null(registry.CouplingOf(Session, "diagram-1"));
    }

    [Fact]
    public async Task EditDiagram_WhenWideningIsDenied_LeavesTheReadAccessItAlreadyHad()
    {
        var registry = new DiagramAccessRegistry();
        var host = Substitute.For<ICockpitHost, IWindowProbe>();
        host.CurrentMcpCallerPaneId.Returns((string?)null);
        var outcomes = new Queue<ConsentOutcome>([ConsentOutcome.Approved, ConsentOutcome.Denied]);
        host.RequestConsentAsync(Arg.Any<ConsentRequest>())
            .Returns(_ => new ConsentDecision(outcomes.Dequeue()));
        var tools = new DiagramMcpTools(host, registry, new backend::Cockpit.Plugin.Diagram.DiagramSettings(new FakePluginStorage()), TestChannel.Desktop(host, diagrams: registry));
        registry.SurfaceOpened("diagram-1", "Onboarding flow", Source);
        await tools.ReadDiagram(Session, "Onboarding flow");

        var json = JsonNode.Parse(await tools.EditDiagram(Session, "Onboarding flow", "flowchart LR\nA-->B-->C"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("still be able to read", json["error"]!.GetValue<string>());
        var coupling = registry.CouplingOf(Session, "diagram-1");
        Assert.True(coupling!.CanRead);
        Assert.False(coupling.CanEdit);
        Assert.Equal(Source, registry.PeekText("diagram-1")); // untouched
    }

    [Fact]
    public async Task EditDiagram_WhenDenied_DoesNotWrite()
    {
        var (tools, registry, _, _) = _Build(ConsentOutcome.Denied);
        registry.SurfaceOpened("diagram-1", "Onboarding flow", Source);

        var json = JsonNode.Parse(await tools.EditDiagram(Session, "Onboarding flow", "flowchart LR\nA-->B-->C"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Equal(Source, registry.PeekText("diagram-1"));
        Assert.Null(registry.CouplingOf(Session, "diagram-1"));
        Assert.Null(registry.PendingProposal("diagram-1"));
    }

    [Fact]
    public async Task PerObjectEdit_KeepsWhatTheOperatorChangedInTheMeantime_InsteadOfOverwritingTheWholeDiagram()
    {
        // The lost-update AC-852 exists to end: the agent never re-sends a whole source, so a hand edit that
        // landed between two agent calls is still there afterwards.
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("diagram-1", "Onboarding flow", "flowchart LR\n    A[\"Start\"]");
        await tools.AddNode(Session, "Onboarding flow", "B", "Stop");

        registry.UpdateText("diagram-1", registry.PeekText("diagram-1") + "\n    C[\"Operator's own\"]");
        await tools.RenameNode(Session, "Onboarding flow", "A", "Begin");

        var text = registry.PeekText("diagram-1");
        Assert.Contains("C[\"Operator's own\"]", text);
        Assert.Contains("A[\"Begin\"]", text);
    }

    [Fact]
    public async Task PerObjectEdit_OnAnObjectTheOperatorIsHolding_IsRefusedWithAReason_WhileOtherObjectsStillEdit()
    {
        // The ticket's own test: the agent renames A while the operator has B under their hand (D-5's "jij
        // bewerkt" marking) — the rename lands, the call naming B is refused and changes nothing.
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("diagram-1", "Onboarding flow", "flowchart LR\n    A[\"Start\"]\n    B[\"Stop\"]");
        registry.HoldObject("diagram-1", "B");

        var renamedA = JsonNode.Parse(await tools.RenameNode(Session, "Onboarding flow", "A", "Begin"));
        var refusedB = JsonNode.Parse(await tools.RenameNode(Session, "Onboarding flow", "B", "Halt"));

        Assert.True(renamedA!["ok"]!.GetValue<bool>());
        Assert.False(refusedB!["ok"]!.GetValue<bool>());
        Assert.Contains("operator is editing", refusedB["error"]!.GetValue<string>());
        Assert.Equal("flowchart LR\n    A[\"Begin\"]\n    B[\"Stop\"]", registry.PeekText("diagram-1"));

        // And the agent can simply try again once the operator lets go.
        registry.ReleaseObject("diagram-1", "B");
        Assert.True(JsonNode.Parse(await tools.RenameNode(Session, "Onboarding flow", "B", "Halt"))!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task WhenAnotherAgentTakesTheSurfaceWhileTheOperatorDecides_TheRefusalIsAnErrorNotAnException()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("diagram-1", "Onboarding flow", Source);
        var host = Substitute.For<ICockpitHost, IWindowProbe>();
        host.CurrentMcpCallerPaneId.Returns((string?)null);
        host.RequestConsentAsync(Arg.Any<ConsentRequest>())
            .Returns(_ =>
            {
                registry.Grant("someone-else", "diagram-1", DiagramCapability.Read); // slipped in while we asked
                return new ConsentDecision(ConsentOutcome.Approved);
            });
        var tools = new DiagramMcpTools(host, registry, new backend::Cockpit.Plugin.Diagram.DiagramSettings(new FakePluginStorage()), TestChannel.Desktop(host, diagrams: registry));

        var json = JsonNode.Parse(await tools.ReadDiagram(Session, "Onboarding flow"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("no longer available", json["error"]!.GetValue<string>());
    }
}
