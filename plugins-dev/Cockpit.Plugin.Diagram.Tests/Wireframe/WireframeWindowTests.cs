using Cockpit.Plugin.Diagram.Wireframe;

namespace Cockpit.Plugin.Diagram.Tests.Wireframe;

// AC-873 DoD: single-instance per *document*, never per session — DiagramWindowTests'/WhiteboardWindowTests'
// counterpart.
public class WireframeWindowTests
{
    [Fact]
    public void KeyFor_IsTheSameForOneDocumentAndDifferentForAnother()
    {
        var one = new WireframeDocument("/memory/Wireframes/instellingen.md", "Instellingen", "screen \"Instellingen\"");
        var two = new WireframeDocument("/memory/Wireframes/login.md", "Login", "screen \"Login\"");

        Assert.Equal(WireframeWindow.KeyFor(one.Id), WireframeWindow.KeyFor(one.Id));
        Assert.NotEqual(WireframeWindow.KeyFor(one.Id), WireframeWindow.KeyFor(two.Id));
    }

    [Fact]
    public void New_GivesEveryUnsavedWireframeItsOwnIdentity()
    {
        Assert.NotEqual(WireframeDocument.New("Nieuw wireframe").Id, WireframeDocument.New("Nieuw wireframe").Id);
    }

    [Fact]
    public void New_WithNoSource_OpensWithASingleChildlessScreen()
    {
        var document = WireframeDocument.New("Mijn wireframe");

        Assert.Equal("Mijn wireframe", document.Title);
        Assert.Equal(WireframeDocument.Empty, document.Text);
    }

    [Fact]
    public void New_WithASource_OpensWithThatSource()
    {
        var document = WireframeDocument.New("Mijn wireframe", "screen \"Login\"");

        Assert.Equal("screen \"Login\"", document.Text);
    }
}
