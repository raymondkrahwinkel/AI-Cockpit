using Cockpit.Plugin.Whiteboard.Model;

namespace Cockpit.Plugin.Whiteboard.Tests.Model;

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

    [Fact]
    public void FreehandAndPlaced_AreDistinguishableByKind_NotJustByType()
    {
        var freehand = new FreehandStroke { Points = [new WhiteboardPoint(0, 0), new WhiteboardPoint(1, 1)] };
        var placed = new PlacedObject { ShapeKind = PlacedShapeKind.Text };

        Assert.Equal(WhiteboardObjectKind.Freehand, freehand.Kind);
        Assert.Equal(WhiteboardObjectKind.Placed, placed.Kind);

        var document = new WhiteboardDocument();
        document.Add(freehand);
        document.Add(placed);

        Assert.Equal(WhiteboardObjectKind.Freehand, document.Objects.Single(o => o.Id == freehand.Id).Kind);
        Assert.Equal(WhiteboardObjectKind.Placed, document.Objects.Single(o => o.Id == placed.Id).Kind);
    }
}
