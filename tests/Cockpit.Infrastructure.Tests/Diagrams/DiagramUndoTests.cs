using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Infrastructure.Diagrams;

namespace Cockpit.Infrastructure.Tests.Diagrams;

/// <summary>
/// AC-853: the vangnet that replaces the diff-poort for the per-object tools (AC-852) and the operator's own
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
    public void Revert_IsMarkedOnTheEntry_NotErasedFromHistory()
    {
        // "Verdwijnt niet uit de geschiedenis alsof het nooit gebeurd is" — the row stays, flagged.
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart TD");
        registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.AddNode, "A", Label: "Start"));
        var entry = Assert.Single(registry.History("surface-1"));

        registry.Revert("surface-1", entry.Id);

        var after = Assert.Single(registry.History("surface-1"));
        Assert.Equal(entry.Id, after.Id);
        Assert.True(after.Reverted);
    }

    [Fact]
    public void Revert_TheSameEntryTwice_IsRefusedTheSecondTime()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart TD");
        registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.AddNode, "A", Label: "Start"));
        var entry = Assert.Single(registry.History("surface-1"));
        Assert.Null(registry.Revert("surface-1", entry.Id));

        var refusal = registry.Revert("surface-1", entry.Id);

        Assert.NotNull(refusal);
    }

    [Fact]
    public void Revert_RemoveNode_RestoresTheNodeAndItsConnection()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart TD\n    A[\"Start\"]\n    B[\"Eind\"]\n    A --> B");
        registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.RemoveNode, "A"));
        var entry = registry.History("surface-1").Single(candidate => candidate.Kind == DiagramHandEditKind.RemoveNode);

        Assert.Null(registry.Revert("surface-1", entry.Id));

        var text = registry.PeekText("surface-1")!;
        Assert.Contains("A[\"Start\"]", text, StringComparison.Ordinal);
        Assert.Contains("A --> B", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Revert_RenameNode_RestoresThePreviousLabel_EvenAfterALaterEditOnAnotherNode()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart TD\n    A[\"Start\"]");
        registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.RenameNode, "A", Label: "Begin"));
        var entry = Assert.Single(registry.History("surface-1"));
        registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.AddNode, "C", Label: "Later"));

        Assert.Null(registry.Revert("surface-1", entry.Id));

        var text = registry.PeekText("surface-1")!;
        Assert.Contains("A[\"Start\"]", text, StringComparison.Ordinal);
        Assert.Contains("C[\"Later\"]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Revert_Connect_DisconnectsTheTwoNodesAndLeavesThemStanding()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart TD\n    A[\"Start\"]\n    B[\"Eind\"]");
        registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.Connect, "A", To: "B"));
        var entry = Assert.Single(registry.History("surface-1"));

        Assert.Null(registry.Revert("surface-1", entry.Id));

        var text = registry.PeekText("surface-1")!;
        Assert.DoesNotContain("A --> B", text, StringComparison.Ordinal);
        Assert.Contains("A[\"Start\"]", text, StringComparison.Ordinal);
        Assert.Contains("B[\"Eind\"]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Revert_Disconnect_ReconnectsWithTheOriginalLabel()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Onboarding flow", "flowchart TD\n    A[\"Start\"]\n    B[\"Eind\"]\n    A -->|\"gaat naar\"| B");
        registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.Disconnect, "A", To: "B"));
        var entry = Assert.Single(registry.History("surface-1"));

        Assert.Null(registry.Revert("surface-1", entry.Id));

        Assert.Contains("A -->|\"gaat naar\"| B", registry.PeekText("surface-1"), StringComparison.Ordinal);
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
