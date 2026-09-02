using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Infrastructure.Diagrams;

namespace Cockpit.Infrastructure.Tests.Diagrams;

/// <summary>
/// AC-853: the safety net that replaces the diff gate for the per-object tools (AC-852) and the operator's own
/// hand-edits (AC-841) — a targeted revert per journaled entry, never "undo the last change", because there are two
/// writers and one's undo must not discard the other's later work.
/// </summary>
public class DiagramUndoTests
{
    private static bool EditCoupled(DiagramAccessRegistry registry, string session, string surface, DiagramHandEditKind kind, string objectKey, Func<string, DiagramEdit> edit) =>
        registry.EditCoupled(session, surface, kind, objectKey, source =>
        {
            var result = edit(source);
            return (result.Text, result.Summary);
        });

    [Fact]
    public void Revert_TheAgentsEdit_LeavesTheOperatorsLaterEditOnAnotherObjectStanding()
    {
        // The ticket's own acceptance test: agent edits A, operator edits B, operator reverts the agent's edit on A
        // — B must still be there. A blunt "undo the last change" would get this wrong whenever B landed after A.
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart TD\n    A[\"Start\"]");
        registry.Grant("agent-1", "surface-1", DiagramCapability.Edit);

        EditCoupled(registry, "agent-1", "surface-1", DiagramHandEditKind.AddNode, "A2", source => DiagramObjectEdit.AddNode(source, "A2", "Agent node"));
        var agentEntry = Assert.Single(registry.History("surface-1"));
        registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.AddNode, "B", Label: "Operator node"));

        var refusal = registry.Revert("surface-1", agentEntry.Id);

        Assert.Null(refusal);
        var text = registry.PeekText("surface-1")!;
        Assert.DoesNotContain("A2", text, StringComparison.Ordinal);
        Assert.Contains("B[\"Operator node\"]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Revert_IsMarkedOnTheEntry_NotErasedFromHistory_AndCannotBeDoneTwice()
    {
        // Does not vanish from the history as if it never happened — the row stays, flagged, and that flag is what
        // refuses a second revert instead of undoing whatever stands there now.
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart TD");
        registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.AddNode, "A", Label: "Start"));
        var entry = Assert.Single(registry.History("surface-1"));

        Assert.Null(registry.Revert("surface-1", entry.Id));

        var after = Assert.Single(registry.History("surface-1"));
        Assert.Equal(entry.Id, after.Id);
        Assert.True(after.Reverted);
        Assert.NotNull(registry.Revert("surface-1", entry.Id));
    }

    // Every hand-edit that changes a line in place, reverted on its own, against the source it started from —
    // asserted whole rather than by the one line each happens to touch, so a revert that writes the right text on the
    // wrong line fails here. A removal is not among them: putting a block back appends it (see the test below).
    public static TheoryData<string, DiagramHandEdit> RevertedHandEdits() => new()
    {
        { TwoNodesConnected, new DiagramHandEdit(DiagramHandEditKind.RenameNode, "A", Label: "Begin") },
        { TwoNodesConnected, new DiagramHandEdit(DiagramHandEditKind.Disconnect, "A", To: "B") },
        { TwoNodesConnected, new DiagramHandEdit(DiagramHandEditKind.RelabelConnection, "A", To: "B", Label: "gaat naar") },
        { TwoNodesLabelledConnection, new DiagramHandEdit(DiagramHandEditKind.Disconnect, "A", To: "B") },
        { TwoNodesLabelledConnection, new DiagramHandEdit(DiagramHandEditKind.RelabelConnection, "A", To: "B", Label: "loopt naar") },
        { TwoNodesApart, new DiagramHandEdit(DiagramHandEditKind.Connect, "A", To: "B") },
    };

    [Theory]
    [MemberData(nameof(RevertedHandEdits))]
    public void EveryKindOfHandEdit_IsTakenBackToTheSourceItStartedFrom(string source, DiagramHandEdit edit)
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", source);
        Assert.Null(registry.ApplyHandEdit("surface-1", edit));

        Assert.Null(registry.Revert("surface-1", registry.History("surface-1")[^1].Id));

        Assert.Equal(source, registry.PeekText("surface-1"));
    }

    private const string TwoNodesApart = """
        flowchart TD
            A["Start"]
            B["Eind"]
        """;

    private const string TwoNodesConnected = $"""
        {TwoNodesApart}
            A --> B
        """;

    private const string TwoNodesLabelledConnection = $"""
        {TwoNodesApart}
            A -->|"gaat naar"| B
        """;

    // A removal is the one revert that does not restore the source verbatim: the node and its connection come back,
    // but appended rather than in the place the block was taken from. Asserted for what it is rather than for what
    // the name of the operation suggests.
    [Fact]
    public void Revert_RemoveNode_BringsTheNodeAndItsConnectionBack_ThoughNotToTheLineTheyStoodOn()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", TwoNodesConnected);
        registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.RemoveNode, "A"));

        Assert.Null(registry.Revert("surface-1", registry.History("surface-1")[^1].Id));

        var text = registry.PeekText("surface-1")!;
        Assert.Contains("""A["Start"]""", text, StringComparison.Ordinal);
        Assert.Contains("A --> B", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Revert_SetNodeShape_RestoresThePreviousShape_KeepingWhateverLabelIsThereNow()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart TD\n    A[\"Start\"]");
        registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.SetNodeShape, "A") { Shape = DiagramNodeShape.Diamond });
        var shapeEntry = Assert.Single(registry.History("surface-1"));
        registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.RenameNode, "A", Label: "Begin"));

        Assert.Null(registry.Revert("surface-1", shapeEntry.Id));

        var text = registry.PeekText("surface-1")!;
        Assert.Contains("A[\"Begin\"]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("A{", text, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryChanged_FiresOnBothJournalingAndReverting()
    {
        var registry = new DiagramAccessRegistry();
        var fired = new List<string>();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart TD");
        registry.HistoryChanged += surfaceId => fired.Add(surfaceId);

        registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.AddNode, "A", Label: "Start"));
        registry.Revert("surface-1", registry.History("surface-1").Single().Id);

        Assert.Equal(["surface-1", "surface-1"], fired);
    }
}
