using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Plugin.Diagram.Whiteboard;

namespace Cockpit.Plugin.Diagram.Tests.Whiteboard;

// W-4/AC-845 DoD: wanneer omzetten uit staat en waarom, wat de statusregel meldt, en dat de gestuurde beurt de
// diff-poort aanwijst in plaats van de directe schrijftools.
public class WhiteboardToDiagramTests
{
    private static WhiteboardCoupling Coupled(bool canRead) => new("pane-1", canRead);

    [Fact]
    public void Blocker_IsNull_OnlyWithALiveSessionThatMayReadTheBoard() =>
        // The one combination that goes through: a live session, on a profile that draws diagrams, allowed to read.
        Assert.Null(WhiteboardToDiagram.Blocker(true, true, Coupled(canRead: true)));

    public static IEnumerable<object[]> Blockers() =>
    [
        [true, true, null!, "No agent coupled"],
        [true, false, Coupled(canRead: true), "No agent coupled"],
        [true, true, Coupled(canRead: false), "may not read"],
        [false, true, Coupled(canRead: true), "does not draw diagrams"],
    ];

    [Theory]
    [MemberData(nameof(Blockers))]
    public void Blocker_SaysWhichConditionIsMissing(bool drawsDiagrams, bool hasSession, object? coupling, string expected) =>
        Assert.Contains(expected, WhiteboardToDiagram.Blocker(drawsDiagrams, hasSession, (WhiteboardCoupling?)coupling));

    [Theory]
    // Nothing asked yet, so nothing to report at all.
    [InlineData(false, 0, "")]
    [InlineData(true, 1, "1 conversion proposed")]
    [InlineData(true, 2, "2 conversions proposed")]
    public void Status_ReportsWhatLandedInThePoort_NotWhatWasAsked(bool asked, int proposals, string expected) =>
        Assert.Equal(expected, WhiteboardToDiagram.Status(asked, proposals));

    [Fact]
    public void Status_AskedButNothingProposedYet_SaysItIsWaiting() =>
        Assert.Contains("waiting", WhiteboardToDiagram.Status(asked: true, proposals: 0));

    [Fact]
    public void ConvertPrompt_AsksForOneProposalThroughTheDiffPoort()
    {
        var prompt = WhiteboardToDiagram.ConvertPrompt("plan-schets", "abc123", "plan-schets — diagram");

        Assert.Contains("read_whiteboard", prompt);
        Assert.Contains("edit_diagram", prompt);
        Assert.Contains("abc123", prompt);
        Assert.Contains("diff gate", prompt);

        // The per-object tools write straight through (AC-852) — they must not take this path.
        Assert.Contains("Do not use add_node", prompt);
        Assert.Contains("Do not change anything on the board yourself", prompt);
    }

    [Fact]
    public void WriteDownPrompt_ChangesNothingAtAll()
    {
        var prompt = WhiteboardToDiagram.WriteDownPrompt("plan-schets");

        Assert.Contains("read_whiteboard", prompt);
        Assert.DoesNotContain("edit_diagram", prompt);
        Assert.Contains("do not change anything", prompt);
    }
}
