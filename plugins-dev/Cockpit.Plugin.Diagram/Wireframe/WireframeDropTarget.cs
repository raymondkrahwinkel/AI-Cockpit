using Avalonia;

namespace Cockpit.Plugin.Diagram.Wireframe;

// Where a dragged component would land among a container's children (AC-904), worked out from where those children
// were actually laid out rather than from a second copy of the renderer's row/column rules. Here rather than in the
// panel because it is arithmetic, and an off-by-one drops a component past the wrong neighbour.
internal static class WireframeDropTarget
{
    // How thick the line between two children is drawn, in surface units — the zoom scales it along with everything
    // else on the surface.
    private const double LineThickness = 3;

    // The gap the pointer sits at among `children`, counted the way a move's `position` counts them — 0 before the
    // first, `children.Count` after the last — and the rectangle to draw the line in. There has to be at least one
    // child: an empty container is dropped into as a whole and never asks for an index.
    public static (int Index, Rect Line) Resolve(IReadOnlyList<Rect> children, Rect container, Point pointer)
    {
        var horizontal = _IsHorizontal(children, container);
        var at = horizontal ? pointer.X : pointer.Y;
        var index = children.Count(child => _Centre(child, horizontal) < at);
        var edge = _Edge(children, container, index, horizontal);

        return (index, horizontal
            ? new Rect(edge - (LineThickness / 2), container.Y, LineThickness, container.Height)
            : new Rect(container.X, edge - (LineThickness / 2), container.Width, LineThickness));
    }

    // Where the line goes: midway between the two children it separates, or against the container's own edge at
    // either end — so inserting first or last is drawn inside the container rather than half outside it.
    private static double _Edge(IReadOnlyList<Rect> children, Rect container, int index, bool horizontal)
    {
        if (index == 0)
        {
            return Math.Max(_Start(children[0], horizontal), _Start(container, horizontal) + (LineThickness / 2));
        }

        if (index == children.Count)
        {
            return Math.Min(_End(children[^1], horizontal), _End(container, horizontal) - (LineThickness / 2));
        }

        return (_End(children[index - 1], horizontal) + _Start(children[index], horizontal)) / 2;
    }

    // Which way the container lays its children out. With more than one child their centres say it outright; with a
    // single child its share of the container does — a column's child spans the full width, a row's the full height.
    private static bool _IsHorizontal(IReadOnlyList<Rect> children, Rect container) =>
        children.Count > 1
            ? _Spread(children, horizontal: true) > _Spread(children, horizontal: false)
            : children[0].Height * container.Width > children[0].Width * container.Height;

    private static double _Spread(IReadOnlyList<Rect> children, bool horizontal) =>
        children.Max(child => _Centre(child, horizontal)) - children.Min(child => _Centre(child, horizontal));

    private static double _Centre(Rect rect, bool horizontal) => horizontal ? rect.Center.X : rect.Center.Y;

    private static double _Start(Rect rect, bool horizontal) => horizontal ? rect.X : rect.Y;

    private static double _End(Rect rect, bool horizontal) => horizontal ? rect.Right : rect.Bottom;
}
