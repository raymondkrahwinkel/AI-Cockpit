using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Infrastructure.Diagrams;

namespace Cockpit.Infrastructure.Tests.Diagrams;

/// <summary>
/// The coupling rules behind the diagram-access MCP (AC-810): a surface's text is a state the registry owns from
/// the moment it opens (not a stream captured from the coupling on, unlike terminal), a coupling can exist with
/// zero capabilities, one agent per surface, and a surface close or a session end decouples on its own.
/// </summary>
public class DiagramAccessRegistryTests
{
    [Fact]
    public void Couple_OnItsOwn_GrantsNoCapabilities()
    {
        // AC-810 DoD: coupling without capabilities is a real, visible state — the shape AC-816's quick-start needs.
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");

        registry.Couple("session-a", "surface-1");

        var coupling = registry.CouplingOf("session-a", "surface-1");
        Assert.NotNull(coupling);
        Assert.False(coupling!.CanRead);
        Assert.False(coupling.CanEdit);
        Assert.False(coupling.HasAnyCapability);
        Assert.Null(registry.ReadCoupled("session-a", "surface-1"));
        Assert.False(registry.WriteCoupled("session-a", "surface-1", "x"));
    }

    [Fact]
    public void ReadCoupled_ReturnsTheSurfaceAsItStandsNow_IncludingWhatWasThereBeforeTheCoupling()
    {
        // Deviation from AC-34: a diagram is a state, not a stream — there is no "since the coupling" to read from.
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");

        registry.Couple("session-a", "surface-1");
        registry.Grant("session-a", "surface-1", DiagramCapability.Read);

        Assert.Equal("flowchart LR\nA-->B", registry.ReadCoupled("session-a", "surface-1"));
    }

    [Fact]
    public void Grant_Edit_AlsoGrantsRead()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");

        registry.Grant("session-a", "surface-1", DiagramCapability.Edit);

        var coupling = registry.CouplingOf("session-a", "surface-1");
        Assert.True(coupling!.CanRead);
        Assert.True(coupling.CanEdit);
    }

    [Fact]
    public void Couple_IsExclusive_ASecondAgentIsRefused_EvenAgainstAZeroCapabilityCoupling()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");
        registry.Couple("session-a", "surface-1");

        Assert.True(registry.IsCoupledByAnother("session-b", "surface-1"));
        Assert.Null(registry.CouplingOf("session-b", "surface-1"));
        var act = () => registry.Couple("session-b", "surface-1");
        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void WriteCoupled_UpdatesTheTextAndRaisesTextChanged_ButOnlyForASessionHoldingEdit()
    {
        var registry = new DiagramAccessRegistry();
        var changes = new List<(string SurfaceId, string Text)>();
        registry.TextChanged += (surfaceId, text) => changes.Add((surfaceId, text));
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");
        registry.Grant("session-a", "surface-1", DiagramCapability.Read); // read only, not edit

        Assert.False(registry.WriteCoupled("session-a", "surface-1", "flowchart LR\nA-->C"));
        Assert.Empty(changes);

        registry.Grant("session-a", "surface-1", DiagramCapability.Edit);
        Assert.True(registry.WriteCoupled("session-a", "surface-1", "flowchart LR\nA-->C"));

        Assert.Equal(("surface-1", "flowchart LR\nA-->C"), Assert.Single(changes));
        Assert.Equal("flowchart LR\nA-->C", registry.PeekText("surface-1"));
    }

    [Fact]
    public void PeekText_ReadsRegardlessOfCoupling_SoTheConsentPromptCanNameWhatIsBeingShared()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");

        Assert.Equal("flowchart LR\nA-->B", registry.PeekText("surface-1"));
        Assert.Null(registry.PeekText("no-such-surface"));
    }

    [Fact]
    public void SurfaceClosed_DecouplesAutomatically()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");
        registry.Grant("session-a", "surface-1", DiagramCapability.Edit);

        registry.SurfaceClosed("surface-1");

        Assert.Null(registry.CouplingOf("session-a", "surface-1"));
        Assert.Null(registry.PeekText("surface-1"));
    }

    [Fact]
    public void SessionEnded_DecouplesEverySurfaceThatSessionHeld()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Flow one", "flowchart LR\nA-->B");
        registry.SurfaceOpened("surface-2", "Flow two", "flowchart LR\nC-->D");
        registry.Grant("session-a", "surface-1", DiagramCapability.Read);
        registry.Grant("session-a", "surface-2", DiagramCapability.Read);

        registry.SessionEnded("session-a");

        Assert.Null(registry.CouplingOf("session-a", "surface-1"));
        Assert.Null(registry.CouplingOf("session-a", "surface-2"));
    }

    [Fact]
    public void Resolve_MatchesByIdOrByOperatorFacingName()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");

        Assert.Equal("Onboarding flow", registry.Resolve("surface-1")!.Name);
        Assert.Equal("surface-1", registry.Resolve("Onboarding flow")!.SurfaceId);
        Assert.Null(registry.Resolve("nope"));
    }

    [Fact]
    public void Disconnect_DecouplesWhateverCapabilitiesWereHeld_AndAnnounces()
    {
        var registry = new DiagramAccessRegistry();
        var changes = new List<DiagramCouplingChange>();
        registry.CouplingChanged += changes.Add;
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");
        registry.Grant("session-a", "surface-1", DiagramCapability.Edit);

        registry.Disconnect("surface-1");

        Assert.Null(registry.CouplingOf("session-a", "surface-1"));
        Assert.Equal(2, changes.Count);
        Assert.NotNull(changes[0].Coupling);
        Assert.Null(changes[1].Coupling);
    }

    [Fact]
    public void Grant_RefusesASurfaceThatIsNotOpen()
    {
        var registry = new DiagramAccessRegistry();
        var act = () => registry.Grant("session-a", "never-registered", DiagramCapability.Read);
        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void Propose_RequiresEdit_ReturnsFalse_AndTouchesNothing_WhenTheSessionOnlyHoldsRead()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");
        registry.Grant("session-a", "surface-1", DiagramCapability.Read);

        var accepted = registry.Propose("session-a", "surface-1", "flowchart LR\nA-->C", "1 line changed", []);

        Assert.False(accepted);
        Assert.Null(registry.PendingProposal("surface-1"));
        Assert.Equal("flowchart LR\nA-->B", registry.PeekText("surface-1"));
    }

    [Fact]
    public void Propose_WithEdit_RecordsAPendingProposal_WithoutTouchingTheStoredSource()
    {
        var registry = new DiagramAccessRegistry();
        var proposals = new List<(string SurfaceId, DiagramProposal? Proposal)>();
        registry.ProposalChanged += (surfaceId, proposal) => proposals.Add((surfaceId, proposal));
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");
        registry.Grant("session-a", "surface-1", DiagramCapability.Edit);

        var accepted = registry.Propose("session-a", "surface-1", "flowchart LR\nA-->C", "1 line changed", ["dropped: composite state"]);

        Assert.True(accepted);
        Assert.Equal("flowchart LR\nA-->B", registry.PeekText("surface-1")); // untouched until accepted
        var proposal = registry.PendingProposal("surface-1");
        Assert.NotNull(proposal);
        Assert.Equal("flowchart LR\nA-->C", proposal!.ProposedText);
        Assert.Equal(["dropped: composite state"], proposal.FidelityFindings);
        Assert.NotEmpty(proposal.Blocks);
        Assert.Equal(("surface-1", proposal), Assert.Single(proposals));
    }

    [Fact]
    public void ResolveProposal_AcceptingTheOnlyChangeBlock_WritesTheProposedTextAndClearsTheProposal()
    {
        var registry = new DiagramAccessRegistry();
        var changes = new List<(string SurfaceId, string Text)>();
        registry.TextChanged += (surfaceId, text) => changes.Add((surfaceId, text));
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");
        registry.Grant("session-a", "surface-1", DiagramCapability.Edit);
        registry.Propose("session-a", "surface-1", "flowchart LR\nA-->C", "1 line changed", []);
        var changeBlockIndex = registry.PendingProposal("surface-1")!.Blocks
            .Select((block, index) => (block, index)).Single(x => x.block.IsChange).index;

        var resolved = registry.ResolveProposal("surface-1", new HashSet<int> { changeBlockIndex });

        Assert.True(resolved);
        Assert.Equal("flowchart LR\nA-->C", registry.PeekText("surface-1"));
        Assert.Equal(("surface-1", "flowchart LR\nA-->C"), Assert.Single(changes));
        Assert.Null(registry.PendingProposal("surface-1"));
    }

    [Fact]
    public void ResolveProposal_RejectingTheChangeBlock_NeverWritesItToTheStoredSource()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");
        registry.Grant("session-a", "surface-1", DiagramCapability.Edit);
        registry.Propose("session-a", "surface-1", "flowchart LR\nA-->C", "1 line changed", []);

        // No block index accepted — the fail-closed default keeps every change block's old side.
        var resolved = registry.ResolveProposal("surface-1", new HashSet<int>());

        Assert.True(resolved);
        Assert.Equal("flowchart LR\nA-->B", registry.PeekText("surface-1"));
        Assert.Null(registry.PendingProposal("surface-1"));
    }

    [Fact]
    public void DiscardProposal_ClearsThePendingProposal_WithoutWritingAnything()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");
        registry.Grant("session-a", "surface-1", DiagramCapability.Edit);
        registry.Propose("session-a", "surface-1", "flowchart LR\nA-->C", "1 line changed", []);

        var discarded = registry.DiscardProposal("surface-1");

        Assert.True(discarded);
        Assert.Equal("flowchart LR\nA-->B", registry.PeekText("surface-1"));
        Assert.Null(registry.PendingProposal("surface-1"));
        Assert.False(registry.DiscardProposal("surface-1")); // nothing left to discard
    }

    [Fact]
    public void SurfaceClosed_AndSessionEnded_AlsoClearAnyPendingProposal()
    {
        var registryForClose = new DiagramAccessRegistry();
        registryForClose.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");
        registryForClose.Grant("session-a", "surface-1", DiagramCapability.Edit);
        registryForClose.Propose("session-a", "surface-1", "flowchart LR\nA-->C", "1 line changed", []);

        registryForClose.SurfaceClosed("surface-1");

        Assert.Null(registryForClose.PendingProposal("surface-1"));

        var registryForSessionEnd = new DiagramAccessRegistry();
        registryForSessionEnd.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");
        registryForSessionEnd.Grant("session-a", "surface-1", DiagramCapability.Edit);
        registryForSessionEnd.Propose("session-a", "surface-1", "flowchart LR\nA-->C", "1 line changed", []);

        registryForSessionEnd.SessionEnded("session-a");

        Assert.Null(registryForSessionEnd.PendingProposal("surface-1"));
    }

    [Fact]
    public void UpdateText_FromTheOperator_KeepsWhatAnAgentReadsInStep_AndRaisesTextChanged()
    {
        var registry = new DiagramAccessRegistry();
        var changes = new List<(string SurfaceId, string Text)>();
        registry.TextChanged += (surfaceId, text) => changes.Add((surfaceId, text));
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\nA-->B");
        registry.Grant("session-a", "surface-1", DiagramCapability.Read);

        registry.UpdateText("surface-1", "flowchart LR\nA-->B-->C");

        Assert.Equal("flowchart LR\nA-->B-->C", registry.ReadCoupled("session-a", "surface-1"));
        Assert.Equal(("surface-1", "flowchart LR\nA-->B-->C"), Assert.Single(changes));
    }

    [Fact]
    public void ApplyHandEdit_ChangesOnlyTheObjectItNames_AndSaysWhatItDid()
    {
        var registry = new DiagramAccessRegistry();
        var summaries = new List<string>();
        registry.ObjectEdited += (_, summary) => summaries.Add(summary);
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart TD\n    A[\"Start\"]\n    B[\"Eind\"]\n    A --> B");

        Assert.Null(registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.RenameNode, "A", Label: "Begin")));

        var text = registry.PeekText("surface-1")!;
        Assert.Contains("A[\"Begin\"]", text, StringComparison.Ordinal);
        Assert.Contains("B[\"Eind\"]", text, StringComparison.Ordinal);
        Assert.Contains("A --> B", text, StringComparison.Ordinal);
        Assert.Equal("renamed node A to \"Begin\"", Assert.Single(summaries));
    }

    [Fact]
    public void ApplyHandEdit_AndAnAgentEditOnAnotherObject_BothLand()
    {
        // AC-841: geen verloren wijzigingen — the operator's hand-edit and the agent's per-object edit take the same
        // read-modify-write under the lock, so neither replaces the whole source the other worked in.
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart TD\n    A[\"Start\"]\n    B[\"Eind\"]");
        registry.Grant("session-a", "surface-1", DiagramCapability.Edit);

        registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.RenameNode, "A", Label: "Begin"));
        registry.EditCoupled("session-a", "surface-1", source =>
        {
            var edit = DiagramObjectEdit.RenameNode(source, "B", "Klaar");
            return (edit.Text, edit.Summary);
        });

        var text = registry.PeekText("surface-1")!;
        Assert.Contains("A[\"Begin\"]", text, StringComparison.Ordinal);
        Assert.Contains("B[\"Klaar\"]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyHandEdit_ThatWouldNotLeaveValidMermaid_ChangesNothingAndSaysWhy()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "sequenceDiagram\n    A->>B: hoi");

        // Per-object line surgery only reads flowchart/graph; on anything else it refuses instead of corrupting it.
        var refusal = registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.RemoveNode, "A"));

        Assert.NotNull(refusal);
        Assert.Equal("sequenceDiagram\n    A->>B: hoi", registry.PeekText("surface-1"));
    }

    [Fact]
    public void ApplyHandEdit_OnASurfaceThatIsGone_RefusesRatherThanThrowing()
    {
        var registry = new DiagramAccessRegistry();

        Assert.NotNull(registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.AddNode, "N1", Label: "Nieuw")));
    }
}
