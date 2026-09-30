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

    // Raw string literals take their line endings from the source file on disk (CRLF on a Windows checkout, LF on
    // CI's Linux runners), while the registry always normalizes to "\n" (DiagramAccessRegistry._Lines). Without this,
    // the exact-equality asserts below compare CRLF against LF and fail on every Windows machine, never on CI.
    private static readonly string TwoNodesApart = """
        flowchart TD
            A["Start"]
            B["Eind"]
        """.ReplaceLineEndings("\n");

    private static readonly string TwoNodesConnected = $"""
        {TwoNodesApart}
            A --> B
        """.ReplaceLineEndings("\n");

    private static readonly string TwoNodesLabelledConnection = $"""
        {TwoNodesApart}
            A -->|"gaat naar"| B
        """.ReplaceLineEndings("\n");

}
