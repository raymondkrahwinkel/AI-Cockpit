using Cockpit.Plugin.Diagram.Whiteboard.Model;

namespace Cockpit.Plugin.Diagram.Tests.Whiteboard.Model;

// W-6/AC-851's binding in isolation, no canvas control or pointer events involved — the affine math and the
// point-in-image decision are the two things that have to be exactly right.
public class WhiteboardBindingTests
{
    [Fact]
    public void FindParentImage_ReturnsTheImageThatContainsThePoint()
    {
        var document = new WhiteboardDocument();
        var image = new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 0, Y = 0, Width = 100, Height = 80 };
        document.Add(image);

        var found = WhiteboardBinding.FindParentImage(document, 50, 40);

        Assert.Same(image, found);
    }

    [Fact]
    public void FindParentImage_ReturnsNull_WhenNoImageContainsThePoint()
    {
        var document = new WhiteboardDocument();
        document.Add(new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 0, Y = 0, Width = 100, Height = 80 });

        Assert.Null(WhiteboardBinding.FindParentImage(document, 500, 500));
    }

    [Fact]
    public void FindParentImage_IgnoresNonImageShapes()
    {
        var document = new WhiteboardDocument();
        document.Add(new PlacedObject { ShapeKind = PlacedShapeKind.Rectangle, X = 0, Y = 0, Width = 100, Height = 80 });

        Assert.Null(WhiteboardBinding.FindParentImage(document, 50, 40));
    }

    [Fact]
    public void FindParentImage_PicksTheTopmostImage_WhenTwoOverlap()
    {
        var document = new WhiteboardDocument();
        document.Add(new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 0, Y = 0, Width = 100, Height = 100 });
        var topImage = new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 0, Y = 0, Width = 50, Height = 50 };
        document.Add(topImage);

        Assert.Same(topImage, WhiteboardBinding.FindParentImage(document, 20, 20));
    }

    [Fact]
    public void ChildrenOf_ReturnsOnlyObjectsAnchoredToThatParent()
    {
        var document = new WhiteboardDocument();
        var image = new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 0, Y = 0, Width = 100, Height = 80 };
        document.Add(image);
        var bound = new PlacedObject { ShapeKind = PlacedShapeKind.Text, ParentImageId = image.Id };
        var free = new PlacedObject { ShapeKind = PlacedShapeKind.Text };
        document.Add(bound);
        document.Add(free);

        var children = WhiteboardBinding.ChildrenOf(document, image.Id);

        Assert.Equal([bound], children);
    }

    [Fact]
    public void CarryChildren_TranslatesAPlacedChild_ByTheSameDeltaAsTheParentsMove()
    {
        var document = new WhiteboardDocument();
        var image = new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 100, Y = 100, Width = 100, Height = 100 };
        document.Add(image);
        var label = new PlacedObject { ShapeKind = PlacedShapeKind.Text, X = 120, Y = 130, Width = 20, Height = 10, ParentImageId = image.Id };
        document.Add(label);

        WhiteboardBinding.CarryChildren(document, image.Id, oldX: 100, oldY: 100, oldWidth: 100, oldHeight: 100, newX: 150, newY: 80, newWidth: 100, newHeight: 100);

        Assert.Equal(170, label.X);
        Assert.Equal(110, label.Y);
        Assert.Equal(20, label.Width);
        Assert.Equal(10, label.Height);
    }

    [Fact]
    public void CarryChildren_ScalesAPlacedChild_ProportionallyOnResize()
    {
        var document = new WhiteboardDocument();
        var image = new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 0, Y = 0, Width = 100, Height = 100 };
        document.Add(image);
        // Sits exactly in the image's right half, a quarter of its height down — should stay there after a 2x scale.
        var mark = new PlacedObject { ShapeKind = PlacedShapeKind.Text, X = 50, Y = 25, Width = 10, Height = 10, ParentImageId = image.Id };
        document.Add(mark);

        WhiteboardBinding.CarryChildren(document, image.Id, oldX: 0, oldY: 0, oldWidth: 100, oldHeight: 100, newX: 0, newY: 0, newWidth: 200, newHeight: 200);

        Assert.Equal(100, mark.X);
        Assert.Equal(50, mark.Y);
        Assert.Equal(20, mark.Width);
        Assert.Equal(20, mark.Height);
    }

    [Fact]
    public void CarryChildren_MapsEveryFreehandPoint_KeepingAnOutOfBoundsPointOutOfBounds()
    {
        var document = new WhiteboardDocument();
        var image = new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 0, Y = 0, Width = 100, Height = 100 };
        document.Add(image);
        // Second point sits outside the image on purpose — a partially-out-of-bounds annotation must not be clipped.
        var stroke = new FreehandStroke { Points = [new WhiteboardPoint(50, 50), new WhiteboardPoint(150, 50)], ParentImageId = image.Id };
        document.Add(stroke);

        WhiteboardBinding.CarryChildren(document, image.Id, oldX: 0, oldY: 0, oldWidth: 100, oldHeight: 100, newX: 200, newY: 0, newWidth: 100, newHeight: 100);

        Assert.Equal(new WhiteboardPoint(250, 50), stroke.Points[0]);
        Assert.Equal(new WhiteboardPoint(350, 50), stroke.Points[1]);
    }

    [Fact]
    public void CarryChildren_LeavesUnrelatedObjectsAlone()
    {
        var document = new WhiteboardDocument();
        var image = new PlacedObject { ShapeKind = PlacedShapeKind.Image, X = 0, Y = 0, Width = 100, Height = 100 };
        document.Add(image);
        var unrelated = new PlacedObject { ShapeKind = PlacedShapeKind.Text, X = 500, Y = 500, Width = 10, Height = 10 };
        document.Add(unrelated);

        WhiteboardBinding.CarryChildren(document, image.Id, oldX: 0, oldY: 0, oldWidth: 100, oldHeight: 100, newX: 50, newY: 50, newWidth: 50, newHeight: 50);

        Assert.Equal(500, unrelated.X);
        Assert.Equal(500, unrelated.Y);
    }
}
