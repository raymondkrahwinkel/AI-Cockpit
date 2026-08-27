using Avalonia;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Cockpit.App.Controls;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1126 (out of AC-1120's audit of the five controls with a layout override — <see cref="FocusRailPanel"/>,
/// <see cref="LimitBar"/>, <see cref="MiniatureHost"/>, <see cref="RailTilePanel"/>,
/// <see cref="SessionTilePanel"/>): once a window's layout has settled, nothing should still need a measure or
/// an arrange. Shared here so the same check runs against the whole population instead of one control at a time.
/// </summary>
internal static class LayoutSettledAssertion
{
    public static void AssertSettled(Visual root)
    {
        foreach (var visual in new[] { root }.Concat(root.GetVisualDescendants()))
        {
            if (visual is not Layoutable layoutable)
            {
                continue;
            }

            Assert.True(layoutable.IsMeasureValid, $"{layoutable.GetType().Name} still needs a measure after the window's layout settled");
            Assert.True(layoutable.IsArrangeValid, $"{layoutable.GetType().Name} still needs an arrange after the window's layout settled");
        }
    }
}
