using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Infrastructure.Diagrams;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;

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
        var host = Substitute.For<ICockpitHost>();
        // NSubstitute defaults an unconfigured string-returning member to "", not null — leaving this unset would
        // make `host.CurrentMcpCallerPaneId ?? session` pick "" over the caller-supplied session on every test.
        host.CurrentMcpCallerPaneId.Returns((string?)null);
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add)).Returns(new ConsentDecision(outcome));
        return (new DiagramMcpTools(host, registry), registry, host, asked);
    }

    [Fact]
    public async Task ReadDiagram_FirstTime_AsksConsent_ThenReturnsTheSourceAsItStandsNow()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("diagram-1", "Onboarding flow", Source);

        var json = JsonNode.Parse(await tools.ReadDiagram(Session, "Onboarding flow"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal(Source, json["source"]!.GetValue<string>());
        Assert.Single(asked);
        Assert.Equal(ConsentRisk.Dangerous, asked[0].Risk);
        Assert.Equal("diagram-1", asked[0].Source.PaneId);
        Assert.Contains("Onboarding flow", asked[0].Action);
    }

    [Fact]
    public async Task ReadDiagram_ReflectsAnOperatorEditMadeSinceTheAgentLastRead_WithNoSeparateSyncStep()
    {
        // AC-838: the operator->registry direction. UpdateText is the operator's own write path (the diagram panel's
        // hand-edit actions), independent of the agent's coupling — the next read_diagram must see it immediately.
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("diagram-1", "Onboarding flow", Source);

        var before = JsonNode.Parse(await tools.ReadDiagram(Session, "Onboarding flow"));
        Assert.Equal(Source, before!["source"]!.GetValue<string>());

        const string EditedByOperator = "flowchart LR\nA-->B\nB-->C";
        registry.UpdateText("diagram-1", EditedByOperator);

        var after = JsonNode.Parse(await tools.ReadDiagram(Session, "Onboarding flow"));
        Assert.Equal(EditedByOperator, after!["source"]!.GetValue<string>());
    }

    [Fact]
    public void Coupling_OnItsOwn_GrantsNoCapabilities()
    {
        // AC-810 DoD: the registry supports a "coupled, nothing granted yet" state — the shape AC-816's quick-start
        // needs — and it must not be confused with "never coupled" or with holding read/edit.
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("diagram-1", "Onboarding flow", Source);

        registry.Couple(Session, "diagram-1");

        var coupling = registry.CouplingOf(Session, "diagram-1");
        Assert.NotNull(coupling);
        Assert.False(coupling!.HasAnyCapability);
        Assert.True(registry.IsCoupledByAnother("someone-else", "diagram-1"));
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
    public async Task ReadDiagram_UnknownSurface_ReturnsError_WithoutAsking()
    {
        var (tools, _, _, asked) = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.ReadDiagram(Session, "ghost"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("No such diagram", json["error"]!.GetValue<string>());
        Assert.Empty(asked);
    }

    [Fact]
    public async Task ReadDiagram_WhenSurfaceCoupledToAnotherAgent_IsRefused_WithoutAsking()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("diagram-1", "Onboarding flow", Source);
        registry.Grant("other-agent", "diagram-1", DiagramCapability.Read);

        var json = JsonNode.Parse(await tools.ReadDiagram(Session, "Onboarding flow"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("another agent", json["error"]!.GetValue<string>());
        Assert.Empty(asked);
    }

    [Fact]
    public async Task EditDiagram_AsksOnlyOnce_CoveringReadToo_LikeTerminalsDrive()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("diagram-1", "Onboarding flow", Source);

        var json = JsonNode.Parse(await tools.EditDiagram(Session, "Onboarding flow", "flowchart LR\nA-->B-->C"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.True(json["proposed"]!.GetValue<bool>());
        Assert.Single(asked);
        Assert.Equal("diagram.edit", asked[0].Scope);
        var coupling = registry.CouplingOf(Session, "diagram-1");
        Assert.True(coupling!.CanRead);
        Assert.True(coupling.CanEdit);

        // AC-825: approving edit_diagram lets the agent propose — it does not write anything by itself.
        Assert.Equal(Source, registry.PeekText("diagram-1"));
        var proposal = registry.PendingProposal("diagram-1");
        Assert.NotNull(proposal);
        Assert.Equal("flowchart LR\nA-->B-->C", proposal!.ProposedText);
    }

    [Fact]
    public async Task EditDiagram_AfterOnlyReading_AsksASecondTimeToWiden_ThenProposes()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("diagram-1", "Onboarding flow", Source);
        await tools.ReadDiagram(Session, "Onboarding flow"); // read only

        var json = JsonNode.Parse(await tools.EditDiagram(Session, "Onboarding flow", "flowchart LR\nA-->B-->C"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal(2, asked.Count);
        Assert.Equal("diagram.edit", asked[1].Scope);
        Assert.Contains("now wants to edit it", asked[1].Title);
    }

    [Fact]
    public async Task EditDiagram_WhenWideningIsDenied_LeavesTheReadAccessItAlreadyHad()
    {
        var registry = new DiagramAccessRegistry();
        var host = Substitute.For<ICockpitHost>();
        host.CurrentMcpCallerPaneId.Returns((string?)null);
        var outcomes = new Queue<ConsentOutcome>([ConsentOutcome.Approved, ConsentOutcome.Denied]);
        host.RequestConsentAsync(Arg.Any<ConsentRequest>())
            .Returns(_ => new ConsentDecision(outcomes.Dequeue()));
        var tools = new DiagramMcpTools(host, registry);
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
    public async Task EditDiagram_ConsentText_IsDerivedFromTheActualChange_NotFromAgentSuppliedProse()
    {
        // AC-489's requirement, restated for AC-810: the sentence is built from the real diff, so an agent cannot
        // phrase its own edit as smaller or safer than it is by writing a misleading `source` around it.
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("diagram-1", "Onboarding flow", "flowchart LR\nA-->B\nB-->C\nC-->D");

        await tools.EditDiagram(Session, "Onboarding flow", "flowchart LR\nA-->B\nB-->E");

        Assert.Contains("1 line added, 2 lines removed", asked[0].Action);
    }

    [Fact]
    public async Task EditDiagram_OnANewSurfaceWithNoPriorText_ReportsItAsWrittenForTheFirstTime()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("diagram-1", "Blank canvas", "");

        await tools.EditDiagram(Session, "Blank canvas", "flowchart LR\nA-->B");

        Assert.Contains("written for the first time (2 lines)", asked[0].Action);
    }

    [Fact]
    public async Task EditDiagram_CarriesTheFidelityReport_OnTheProposalItself_BeforeAcceptance()
    {
        // AC-825's DoD: the AC-808 report must be visible on the proposal, not only on the result afterwards.
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        const string composite = """
            stateDiagram-v2
                state Watching {
                    [*] --> Idle
                }
                Idle --> Watching : arm
            """;
        registry.SurfaceOpened("diagram-1", "State machine", Source);

        var json = JsonNode.Parse(await tools.EditDiagram(Session, "State machine", composite));

        Assert.False(json!["fidelity"]!["complete"]!.GetValue<bool>());
        var proposal = registry.PendingProposal("diagram-1");
        Assert.NotEmpty(proposal!.FidelityFindings);
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
    public async Task PerObjectEdit_AppliesStraightAway_UnderTheSameEditConsentAsEditDiagram()
    {
        // AC-852: with the diff gate gone for continuous editing (Q1), a per-object call writes through — but the
        // Edit capability is still asked for once, exactly as edit_diagram asks for it.
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("diagram-1", "Onboarding flow", "flowchart LR\n    A[\"Start\"]");
        var summaries = new List<string>();
        registry.ObjectEdited += (_, summary) => summaries.Add(summary);

        var json = JsonNode.Parse(await tools.AddNode(Session, "Onboarding flow", "B", "Stop"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Single(asked);
        Assert.Equal("diagram.edit", asked[0].Scope);
        Assert.Equal("flowchart LR\n    A[\"Start\"]\n    B[\"Stop\"]", registry.PeekText("diagram-1"));
        Assert.Null(registry.PendingProposal("diagram-1"));

        // AC-848's line per handling: what changed, not "the whole source was replaced".
        Assert.Equal(["added node B \"Stop\""], summaries);
        Assert.Equal("added node B \"Stop\"", json["changed"]!.GetValue<string>());
    }

    [Fact]
    public async Task RelabelConnection_ChangesTheLabel_UnderTheSameEditConsent()
    {
        // AC-909: the agent side of the symmetry gap — connect_nodes could already carry a label, relabel_connection
        // is what lets it change one afterwards, the way the operator's own relabel box does.
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("diagram-1", "Onboarding flow", "flowchart LR\n    A[\"Start\"]\n    B[\"Stop\"]\n    A --> B");

        var json = JsonNode.Parse(await tools.RelabelConnection(Session, "Onboarding flow", "A", "B", "go"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Contains("A -->|\"go\"| B", registry.PeekText("diagram-1"));
    }

    [Fact]
    public async Task SetNodeShape_ChangesTheShape_KeepingTheLabel()
    {
        // AC-909: the agent side of the shape symmetry gap — add_node always wrote a rectangle, set_node_shape is
        // what lets it (or the operator's own pick) change afterwards.
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("diagram-1", "Onboarding flow", "flowchart LR\n    A[\"Start\"]");

        var json = JsonNode.Parse(await tools.SetNodeShape(Session, "Onboarding flow", "A", "diamond"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Contains("A{\"Start\"}", registry.PeekText("diagram-1"));
    }

    [Fact]
    public async Task SetNodeShape_WithAnUnknownShapeName_IsRefused_WithoutAsking()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("diagram-1", "Onboarding flow", "flowchart LR\n    A[\"Start\"]");

        var json = JsonNode.Parse(await tools.SetNodeShape(Session, "Onboarding flow", "A", "hexagon"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Empty(asked);
        Assert.Equal("flowchart LR\n    A[\"Start\"]", registry.PeekText("diagram-1"));
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
    public async Task PerObjectEdit_WhenDenied_ChangesNothing()
    {
        var (tools, registry, _, _) = _Build(ConsentOutcome.Denied);
        registry.SurfaceOpened("diagram-1", "Onboarding flow", Source);

        var json = JsonNode.Parse(await tools.ConnectNodes(Session, "Onboarding flow", "A", "C"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Equal(Source, registry.PeekText("diagram-1"));
    }

    [Fact]
    public async Task PerObjectEdit_ThatCouldNotBeApplied_SaysWhy_AndLeavesTheSourceAlone()
    {
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("diagram-1", "Onboarding flow", Source);

        var json = JsonNode.Parse(await tools.RemoveNode(Session, "Onboarding flow", "Ghost"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("no node", json["error"]!.GetValue<string>());
        Assert.Equal(Source, registry.PeekText("diagram-1"));
    }

    [Fact]
    public void ListDiagrams_ReturnsOpenSurfaces_WithCapabilityFlags()
    {
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("diagram-1", "Onboarding flow", Source);
        registry.Grant(Session, "diagram-1", DiagramCapability.Read);
        registry.SurfaceOpened("diagram-2", "Deploy pipeline", Source);

        var json = JsonNode.Parse(tools.ListDiagrams(Session));

        Assert.True(json!["ok"]!.GetValue<bool>());
        var names = json["diagrams"]!.AsArray().Select(d => d!["name"]!.GetValue<string>()).ToList();
        Assert.Equivalent(new object[] { "Onboarding flow", "Deploy pipeline" }, names);
        var coupled = json["diagrams"]!.AsArray().First(d => d!["name"]!.GetValue<string>() == "Onboarding flow");
        Assert.True(coupled!["canRead"]!.GetValue<bool>());
        Assert.False(coupled["canEdit"]!.GetValue<bool>());
    }

    [Fact]
    public async Task WhenAnotherAgentTakesTheSurfaceWhileTheOperatorDecides_TheRefusalIsAnErrorNotAnException()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("diagram-1", "Onboarding flow", Source);
        var host = Substitute.For<ICockpitHost>();
        host.CurrentMcpCallerPaneId.Returns((string?)null);
        host.RequestConsentAsync(Arg.Any<ConsentRequest>())
            .Returns(_ =>
            {
                registry.Grant("someone-else", "diagram-1", DiagramCapability.Read); // slipped in while we asked
                return new ConsentDecision(ConsentOutcome.Approved);
            });
        var tools = new DiagramMcpTools(host, registry);

        var json = JsonNode.Parse(await tools.ReadDiagram(Session, "Onboarding flow"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("no longer available", json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReadDiagram_CarriesTheFidelityReport_SoAnIncompleteRenderIsNeverDescribedAsClean()
    {
        // AC-808's contract, carried through the MCP surface (AC-810's DoD point 3): a stateDiagram-v2 composite
        // transition that Mermaider is known to drop must show up in the tool's own response, not just on the
        // operator's screen.
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        const string composite = """
            stateDiagram-v2
                state Watching {
                    [*] --> Idle
                }
                Idle --> Watching : arm
            """;
        registry.SurfaceOpened("diagram-1", "State machine", composite);

        var json = JsonNode.Parse(await tools.ReadDiagram(Session, "State machine"));

        Assert.False(json!["fidelity"]!["complete"]!.GetValue<bool>());
        Assert.NotEmpty(json["fidelity"]!["findings"]!.AsArray());
    }

    [Fact]
    public async Task ReadDiagram_OfACleanDiagram_ReportsFidelityAsComplete_WithNoFindings()
    {
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("diagram-1", "Onboarding flow", Source);

        var json = JsonNode.Parse(await tools.ReadDiagram(Session, "Onboarding flow"));

        Assert.True(json!["fidelity"]!["complete"]!.GetValue<bool>());
        Assert.Empty(json["fidelity"]!["findings"]!.AsArray());
    }

    // ---- open_diagram (AC-835, direct path since AC-891): the agent asks for a window of its own ----

    [Fact]
    public async Task OpenDiagram_WhenApproved_AsksTheOperator_ThenOpensTheWindowDirectly()
    {
        var (tools, _, host, asked) = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.OpenDiagram(Session, "Onboarding flow", Source));
        Dispatcher.UIThread.RunJobs();

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Single(asked);
        Assert.Equal("diagram.open", asked[0].Scope);
        Assert.Equal(ConsentRisk.Dangerous, asked[0].Risk);
        Assert.Contains("Onboarding flow", asked[0].Action);

        var surfaceId = json["id"]!.GetValue<string>();
        await host.Received(1).ShowDialogAsync("Onboarding flow", Arg.Any<Func<Control>>(),
            $"diagram.document.{surfaceId}", Arg.Any<double>(), Arg.Any<double>());
    }

    [Fact]
    public async Task OpenDiagram_WhenDenied_OpensNothing_AndSaysSo()
    {
        var (tools, _, host, asked) = _Build(ConsentOutcome.Denied);

        var json = JsonNode.Parse(await tools.OpenDiagram(Session, "Onboarding flow", Source));
        Dispatcher.UIThread.RunJobs();

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("not approved", json["error"]!.GetValue<string>());
        Assert.Single(asked);
        await host.DidNotReceive().ShowDialogAsync(Arg.Any<string>(), Arg.Any<Func<Control>>(),
            Arg.Any<string>(), Arg.Any<double>(), Arg.Any<double>());
    }

    [Fact]
    public async Task OpenDiagram_WithSourceTheEngineCannotDraw_IsRefused_WithoutAsking()
    {
        var (tools, _, _, asked) = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.OpenDiagram(Session, "Onboarding flow", "this is not mermaid at all"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Empty(asked);
    }
}
