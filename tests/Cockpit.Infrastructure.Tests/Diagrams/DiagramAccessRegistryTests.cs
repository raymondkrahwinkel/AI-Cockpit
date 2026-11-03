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
    public void AHandEditUnderAWaitingProposal_RebasesIt_SoResolvingNeverOverwritesTheOperatorsWork()
    {
        // AC-845: de omzetting bord→diagram wacht in de poort terwijl de operator doorwerkt (AC-841). Wat hij niet
        // aanneemt moet zijn eigen, nieuwste tekst blijven — niet de tekst van toen het voorstel binnenkwam.
        var registry = new DiagramAccessRegistry();
        var announced = new List<DiagramProposal?>();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\n    A-->B");
        registry.Grant("session-a", "surface-1", DiagramCapability.Edit);
        registry.Propose("session-a", "surface-1", "flowchart LR\n    A-->C", "1 line changed", []);
        registry.ProposalChanged += (_, proposal) => announced.Add(proposal);

        Assert.Null(registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.AddNode, "N1", Label: "Handwerk")));
        var resolved = registry.ResolveProposal("surface-1", new HashSet<int>());

        Assert.True(resolved);
        Assert.Contains("Handwerk", registry.PeekText("surface-1"));
        Assert.NotNull(announced[0]); // de poort toont het voorstel opnieuw, tegen de nieuwe bron
        Assert.Null(announced[1]); // en daarna leeg, want het is toegepast
    }

    [Fact]
    public void APerObjectAgentEditUnderAWaitingProposal_RebasesItToo()
    {
        // Dezelfde botsing van de andere kant (AC-852): de agent bewerkt één object terwijl zijn eigen omzetting
        // nog in de poort staat.
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart LR\n    A-->B");
        registry.Grant("session-a", "surface-1", DiagramCapability.Edit);
        registry.Propose("session-a", "surface-1", "flowchart LR\n    A-->C", "1 line changed", []);

        registry.EditCoupled("session-a", "surface-1", DiagramHandEditKind.AddNode, "N1", current => (DiagramObjectEdit.AddNode(current, "N1", "Los").Text, "add node"));
        registry.ResolveProposal("surface-1", new HashSet<int>());

        Assert.Contains("Los", registry.PeekText("surface-1"));
    }
}
