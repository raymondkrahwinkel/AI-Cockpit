using Cockpit.Plugin.Diagram.Whiteboard.Model;

namespace Cockpit.Plugin.Diagram.Tests.Whiteboard.Model;

public class WhiteboardDocumentTests
{
    [Fact]
    public void Add_PutsTheObjectInTheDocument()
    {
        var document = new WhiteboardDocument();
        var placed = new PlacedObject { ShapeKind = PlacedShapeKind.Rectangle };

        document.Add(placed);

        Assert.Same(placed, document.Find(placed.Id));
        Assert.Single(document.Objects);
    }

    [Fact]
    public void Remove_TakesTheObjectOutAndReportsWhetherItWasThere()
    {
        var document = new WhiteboardDocument();
        var placed = new PlacedObject { ShapeKind = PlacedShapeKind.Ellipse };
        document.Add(placed);

        Assert.True(document.Remove(placed.Id));
        Assert.Empty(document.Objects);
        Assert.False(document.Remove(placed.Id));
    }

    [Fact]
    public void MovingAPlacedObject_IsJustMutatingItsBounds_AndTheDocumentSeesIt()
    {
        var document = new WhiteboardDocument();
        var placed = new PlacedObject { ShapeKind = PlacedShapeKind.Diamond, X = 10, Y = 10, Width = 50, Height = 40 };
        document.Add(placed);

        var found = Assert.IsType<PlacedObject>(document.Find(placed.Id));
        found.X = 100;
        found.Y = 80;
        found.Width = 60;

        var moved = Assert.IsType<PlacedObject>(document.Find(placed.Id));
        Assert.Equal(100, moved.X);
        Assert.Equal(80, moved.Y);
        Assert.Equal(60, moved.Width);
    }
}
