using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Plugin.Diagram.Whiteboard;

namespace Cockpit.Plugin.Diagram.Tests.Whiteboard;

// W-4/AC-845 DoD: wanneer omzetten uit staat en waarom, wat de statusregel meldt, en dat de gestuurde beurt de
// diff-poort aanwijst in plaats van de directe schrijftools.
public class WhiteboardToDiagramTests
{
    private static WhiteboardCoupling Coupled(bool canRead) => new("pane-1", canRead);

    [Fact]
    public void Blocker_IsNull_OnlyWithALiveSessionThatMayReadTheBoard()
    {
        Assert.Null(WhiteboardToDiagram.Blocker(true, true, Coupled(canRead: true)));

        Assert.Contains("Geen agent gekoppeld", WhiteboardToDiagram.Blocker(true, true, null));
        Assert.Contains("Geen agent gekoppeld", WhiteboardToDiagram.Blocker(true, false, Coupled(canRead: true)));
        Assert.Contains("niet lezen", WhiteboardToDiagram.Blocker(true, true, Coupled(canRead: false)));
        Assert.Contains("geen diagrammen", WhiteboardToDiagram.Blocker(false, true, Coupled(canRead: true)));
    }

    [Fact]
    public void Status_ReportsWhatLandedInThePoort_NotWhatWasAsked()
    {
        Assert.Equal("", WhiteboardToDiagram.Status(asked: false, proposals: 0));
        Assert.Contains("wacht", WhiteboardToDiagram.Status(asked: true, proposals: 0));
        Assert.Equal("1 omzetting voorgesteld", WhiteboardToDiagram.Status(asked: true, proposals: 1));
        Assert.Equal("2 omzettingen voorgesteld", WhiteboardToDiagram.Status(asked: true, proposals: 2));
    }

    [Fact]
    public void ConvertPrompt_AsksForOneProposalThroughTheDiffPoort()
    {
        var prompt = WhiteboardToDiagram.ConvertPrompt("plan-schets", "abc123", "plan-schets — diagram");

        Assert.Contains("read_whiteboard", prompt);
        Assert.Contains("edit_diagram", prompt);
        Assert.Contains("abc123", prompt);
        Assert.Contains("diff-poort", prompt);

        // De per-object-tools schrijven direct (AC-852) — die mogen dit pad niet nemen.
        Assert.Contains("Gebruik hiervoor niet add_node", prompt);
        Assert.Contains("Verander zelf niets op het bord", prompt);
    }

    [Fact]
    public void WriteDownPrompt_ChangesNothingAtAll()
    {
        var prompt = WhiteboardToDiagram.WriteDownPrompt("plan-schets");

        Assert.Contains("read_whiteboard", prompt);
        Assert.DoesNotContain("edit_diagram", prompt);
        Assert.Contains("verander niets", prompt);
    }
}
