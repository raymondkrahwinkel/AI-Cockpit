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
    // Nothing asked yet, so nothing to report at all.
    [InlineData(false, 0, "")]
    [InlineData(true, 1, "1 conversion proposed")]
    [InlineData(true, 2, "2 conversions proposed")]
    public void Status_ReportsWhatLandedInThePoort_NotWhatWasAsked(bool asked, int proposals, string expected) =>
        Assert.Equal(expected, WhiteboardToDiagram.Status(asked, proposals));
}
