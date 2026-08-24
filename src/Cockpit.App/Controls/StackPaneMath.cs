namespace Cockpit.App.Controls;

// AC-1013 (#54 follow-up): pure, UI-free geometry for the vertically-stacked session layout — per-pane
// proportional weights into stacked rectangles, gutter hit-testing, splitter height transfer, and reorder
// drop index. Kept separate from `SessionTilePanel` so fiddly cases are unit-testable without a visual tree.
internal static class StackPaneMath
{
    // A pane's arranged vertical slot: `Top` down by `Height`.
    public readonly record struct Slot(double Top, double Height)
    {
        public double Bottom => Top + Height;
    }

    // Stacks `weights` down a column of `totalHeight`, leaving a
    // `gutter` gap between adjacent panes. Panes split the height left after the gutters
    // in proportion to their weight. Empty input (or non-positive height) yields an empty list.
    public static IReadOnlyList<Slot> Layout(IReadOnlyList<double> weights, double totalHeight, double gutter)
    {
        var count = weights.Count;
        var slots = new List<Slot>(count);
        if (count == 0)
        {
            return slots;
        }

        var content = totalHeight - gutter * (count - 1);
        if (content <= 0)
        {
            // Degenerate (window too short for the gutters): fall back to equal, gutter-free slices so
            // nothing collapses to a negative height.
            var equal = totalHeight / count;
            for (var i = 0; i < count; i++)
            {
                slots.Add(new Slot(i * equal, equal));
            }

            return slots;
        }

        var sum = 0.0;
        for (var i = 0; i < count; i++)
        {
            sum += Math.Max(0, weights[i]);
        }

        if (sum <= 0)
        {
            sum = count;
        }

        var top = 0.0;
        for (var i = 0; i < count; i++)
        {
            var w = Math.Max(0, weights[i]);
            var h = content * (w <= 0 ? 1 : w) / sum;
            slots.Add(new Slot(top, h));
            top += h + gutter;
        }

        return slots;
    }

    // The gutter index (between pane `i` and `i+1`) whose grab band contains `y`, or -1 over pane content.
    // Band = the gutter widened by `grab` on each side, so a thin gutter stays easy to catch.
    public static int GutterAt(IReadOnlyList<Slot> slots, double y, double gutter, double grab)
    {
        for (var i = 0; i < slots.Count - 1; i++)
        {
            var center = slots[i].Bottom + gutter / 2;
            var half = gutter / 2 + grab;
            if (y >= center - half && y <= center + half)
            {
                return i;
            }
        }

        return -1;
    }

    // Moves `pixelDelta` of height across `gutterIndex` (positive grows upper, shrinks lower), returning a
    // fresh weight array. Only the two neighbours change, clamped to `minPixels` so a pane can't be dragged shut.
    public static double[] Resize(
        IReadOnlyList<double> weights,
        int gutterIndex,
        double pixelDelta,
        double contentHeight,
        double minPixels) =>
        Resize(weights, gutterIndex, pixelDelta, contentHeight, minPixels, minPixels);

    // As above, but the two sides of the gutter keep their own minimum — the focus pane and the rail
    // (AC-443) aren't the same shape, so one shared `minPixels` can't describe both.
    public static double[] Resize(
        IReadOnlyList<double> weights,
        int gutterIndex,
        double pixelDelta,
        double contentHeight,
        double minPixelsA,
        double minPixelsB)
    {
        var result = new double[weights.Count];
        for (var i = 0; i < weights.Count; i++)
        {
            result[i] = Math.Max(0, weights[i]);
        }

        if (gutterIndex < 0 || gutterIndex >= weights.Count - 1 || contentHeight <= 0)
        {
            return result;
        }

        var sum = 0.0;
        foreach (var w in result)
        {
            sum += w;
        }

        if (sum <= 0)
        {
            return result;
        }

        var a = gutterIndex;
        var b = gutterIndex + 1;
        var pairWeight = result[a] + result[b];
        var pairPixels = contentHeight * pairWeight / sum;
        if (pairPixels <= 0)
        {
            return result;
        }

        // Keep both panes at their own minimum; if the pair can't fit both minimums, split the shortfall
        // proportionally rather than favouring whichever side happens to be pane `a`.
        var minA = minPixelsA;
        var minB = minPixelsB;
        if (minA + minB > pairPixels)
        {
            var totalMin = minA + minB;
            minA = totalMin > 0 ? pairPixels * minA / totalMin : pairPixels / 2;
            minB = pairPixels - minA;
        }

        var upperPixels = contentHeight * result[a] / sum + pixelDelta;
        upperPixels = Math.Clamp(upperPixels, minA, pairPixels - minB);

        var ratio = upperPixels / pairPixels;
        result[a] = pairWeight * ratio;
        result[b] = pairWeight * (1 - ratio);
        return result;
    }

    // The index of the slot that contains `pos` along the axis — used to pick the grid
    // cell a pointer is hovering. A pointer in a gutter counts as the following slot; before the first or
    // past the last slot clamps to the ends. Empty input yields 0.
    public static int SlotAt(IReadOnlyList<Slot> slots, double pos)
    {
        for (var i = 0; i < slots.Count; i++)
        {
            if (pos < slots[i].Bottom)
            {
                return i;
            }
        }

        return slots.Count == 0 ? 0 : slots.Count - 1;
    }

    // The index `draggedIndex` should occupy when its grip is held at `pointerY`: after every other pane
    // whose vertical centre the pointer has passed. Returns `[0, count-1]`, equal to `draggedIndex` if no move.
    public static int ReorderTarget(IReadOnlyList<Slot> slots, int draggedIndex, double pointerY)
    {
        if (draggedIndex < 0 || draggedIndex >= slots.Count)
        {
            return draggedIndex;
        }

        var passed = 0;
        for (var i = 0; i < slots.Count; i++)
        {
            if (i == draggedIndex)
            {
                continue;
            }

            var center = slots[i].Top + slots[i].Height / 2;
            if (pointerY > center)
            {
                passed++;
            }
        }

        return Math.Clamp(passed, 0, slots.Count - 1);
    }
}
