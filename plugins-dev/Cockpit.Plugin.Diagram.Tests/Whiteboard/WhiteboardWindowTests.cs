using Cockpit.Plugin.Diagram.Whiteboard;
using Cockpit.Plugin.Diagram.Whiteboard.Model;

namespace Cockpit.Plugin.Diagram.Tests.Whiteboard;

// W-2/AC-843 DoD: reopening a saved board brings the same window forward — DiagramWindowTests' counterpart.
public class WhiteboardWindowTests
{
    [Fact]
    public void KeyFor_IsTheSameForOneDocumentAndDifferentForAnother()
    {
        var one = new WhiteboardDocument(id: "/memory/Whiteboards/plan-schets.json", title: "plan-schets");
        var two = new WhiteboardDocument(id: "/memory/Whiteboards/andere.json", title: "andere");

        Assert.Equal(WhiteboardWindow.KeyFor(one.Id), WhiteboardWindow.KeyFor(one.Id));
        Assert.NotEqual(WhiteboardWindow.KeyFor(one.Id), WhiteboardWindow.KeyFor(two.Id));
    }

    [Fact]
    public void UnsavedBoards_EachGetTheirOwnIdentity()
    {
        Assert.NotEqual(new WhiteboardDocument().Id, new WhiteboardDocument().Id);
    }
}
