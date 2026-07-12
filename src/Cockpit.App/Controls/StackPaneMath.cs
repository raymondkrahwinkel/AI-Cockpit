using System;
using System.Collections.Generic;

namespace Cockpit.App.Controls;

/// <summary>
/// Pure, UI-free geometry for the vertically-stacked session layout (#54 follow-up): turning per-pane
/// <b>weights</b> into stacked rectangles, hit-testing the draggable gutters between them, transferring
/// height between two neighbours on a splitter drag, and picking the drop index for a reorder drag.
///
/// Weights are proportional (unitless): only their ratios matter, so a window resize keeps each pane's
/// share instead of snapping back to equal — the arithmetic here normalises by the running sum. Keeping
/// it separate from <see cref="SessionTilePanel"/> lets the fiddly cases (min-height clamping, a drag
/// past a neighbour) be unit-tested without a visual tree.
/// </summary>
internal static class StackPaneMath
{
    /// <summary>A pane's arranged vertical slot: <paramref name="Top"/> down by <paramref name="Height"/>.</summary>
    public readonly record struct Slot(double Top, double Height)
    {
        public double Bottom => Top + Height;
    }

    /// <summary>
    /// Stacks <paramref name="weights"/> down a column of <paramref name="totalHeight"/>, leaving a
    /// <paramref name="gutter"/> gap between adjacent panes. Panes split the height left after the gutters
    /// in proportion to their weight. Empty input (or non-positive height) yields an empty list.
    /// </summary>
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

    /// <summary>
    /// The index of the gutter (between pane <c>i</c> and <c>i+1</c>) whose grab band contains
    /// <paramref name="y"/>, or -1 if the pointer is over pane content rather than a gutter. The band is
    /// the gutter itself widened by <paramref name="grab"/> on each side so a thin gutter is still easy
    /// to catch.
    /// </summary>
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

    /// <summary>
    /// Moves <paramref name="pixelDelta"/> of height across the gutter <paramref name="gutterIndex"/>
    /// (positive grows the upper pane, shrinks the lower) and returns a fresh weight array. Only the two
    /// neighbours change; their combined share is preserved, so the other panes hold their size. Each of
    /// the pair is clamped to <paramref name="minPixels"/> so a pane can't be dragged shut.
    /// </summary>
    public static double[] Resize(
        IReadOnlyList<double> weights,
        int gutterIndex,
        double pixelDelta,
        double contentHeight,
        double minPixels)
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

        // Keep both panes at least minPixels; if the pair itself can't fit two minimums, split it evenly.
        var min = Math.Min(minPixels, pairPixels / 2);
        var upperPixels = contentHeight * result[a] / sum + pixelDelta;
        upperPixels = Math.Clamp(upperPixels, min, pairPixels - min);

        var ratio = upperPixels / pairPixels;
        result[a] = pairWeight * ratio;
        result[b] = pairWeight * (1 - ratio);
        return result;
    }

    /// <summary>
    /// The index of the slot that contains <paramref name="pos"/> along the axis — used to pick the grid
    /// cell a pointer is hovering. A pointer in a gutter counts as the following slot; before the first or
    /// past the last slot clamps to the ends. Empty input yields 0.
    /// </summary>
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

    /// <summary>
    /// The index the pane at <paramref name="draggedIndex"/> should occupy when its grip is held at
    /// <paramref name="pointerY"/>: it sits after every <i>other</i> pane whose vertical centre the
    /// pointer has passed. Returns a value in <c>[0, count-1]</c>, equal to <paramref name="draggedIndex"/>
    /// when nothing should move.
    /// </summary>
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
