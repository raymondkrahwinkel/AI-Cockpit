using Avalonia;
using Avalonia.Controls;

namespace Cockpit.App.Controls;

/// <summary>
/// Lays out the session panels as a uniform grid, counting only the <b>visible</b> children — so
/// single-pane mode (#24 / Zoom), where all but the selected panel are collapsed, shows that one panel
/// filling the whole area regardless of its position in the collection. Avalonia's <c>UniformGrid</c>
/// reserves a cell per child by index (collapsed or not), which stranded the selected session in its
/// own row (top or bottom half) with the hidden one's row left empty. One visible child fills; two or
/// more tile in two columns (so 3–4 form a 2×2), matching the old grid's column rule.
/// </summary>
public sealed class SessionTilePanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, rows) = Dimensions(VisibleCount());
        if (columns == 0)
        {
            foreach (var child in Children)
            {
                child.Measure(default);
            }

            return default;
        }

        var cell = new Size(availableSize.Width / columns, availableSize.Height / rows);
        foreach (var child in Children)
        {
            child.Measure(child.IsVisible ? cell : default);
        }

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, rows) = Dimensions(VisibleCount());
        if (columns == 0)
        {
            foreach (var child in Children)
            {
                child.Arrange(default);
            }

            return finalSize;
        }

        var cellWidth = finalSize.Width / columns;
        var cellHeight = finalSize.Height / rows;
        var index = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible)
            {
                child.Arrange(default);
                continue;
            }

            var row = index / columns;
            var column = index % columns;
            child.Arrange(new Rect(column * cellWidth, row * cellHeight, cellWidth, cellHeight));
            index++;
        }

        return finalSize;
    }

    private int VisibleCount()
    {
        var count = 0;
        foreach (var child in Children)
        {
            if (child.IsVisible)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Columns/rows for a visible-child count: one fills, two+ use two columns (3–4 → 2×2) — the adaptive rule the multi-session grid has always used.</summary>
    public static (int Columns, int Rows) Dimensions(int visibleCount)
    {
        if (visibleCount <= 0)
        {
            return (0, 0);
        }

        var columns = visibleCount <= 1 ? 1 : 2;
        var rows = (visibleCount + columns - 1) / columns;
        return (columns, rows);
    }
}
