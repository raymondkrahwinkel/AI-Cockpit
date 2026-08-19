using Avalonia;
using Cockpit.Core.Layout;

namespace Cockpit.App.Controls;

// Whether a saved window position is still worth restoring, so a window saved on a monitor that got unplugged
// (or behind a panel/dock) doesn't reopen off in unreachable space. Shared between MainWindow and any other
// window that restores its own bounds (AC-865 [a]) — it used to be a private method on MainWindow alone.
internal static class RestoredWindowBounds
{
    // AC-867: the old check accepted a 1px overlap, so a 99%-off-screen window still counted as "on a
    // screen". This is a rough floor for "an operator could actually grab it" (titlebar height plus a bit
    // of grip width), not a precise UI measurement — callers pass WorkingArea, not Bounds, per screen.
    private const int MinOverlap = 32;

    public static bool IsOnAScreen(WindowBounds bounds, IEnumerable<PixelRect> screenWorkingAreas)
    {
        foreach (var area in screenWorkingAreas)
        {
            var overlapX = Math.Min(bounds.X + bounds.Width, area.X + area.Width) - Math.Max(bounds.X, area.X);
            var overlapY = Math.Min(bounds.Y + bounds.Height, area.Y + area.Height) - Math.Max(bounds.Y, area.Y);
            if (overlapX >= MinOverlap && overlapY >= MinOverlap)
            {
                return true;
            }
        }

        return false;
    }
}
