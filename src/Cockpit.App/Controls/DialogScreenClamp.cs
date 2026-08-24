using Avalonia.Controls;

namespace Cockpit.App.Controls;

// AC-1013: Shrinks a dialog to fit its screen when its desktop-sized layout doesn't, because a dialog
// larger than the screen is centred with nothing to drag it back by and puts its buttons past the bottom
// edge. Details: dropped the plugin store/manage-profiles examples of oversized dialogs.
internal static class DialogScreenClamp
{
    // How much of the screen's working area the dialog may take when its designed size does not fit.
    private const double MaxScreenFraction = 0.9;

    public static void Apply(Window window) => window.Opened += (_, _) => _ClampToScreen(window);

    private static void _ClampToScreen(Window window)
    {
        if (window.Screens.ScreenFromWindow(window) is not { } screen)
        {
            return;
        }

        // WorkingArea is in physical pixels and Width/Height are in DIPs, so the scaling has to come out first
        // or this clamps to the wrong number on any display that is not at 100%.
        var available = screen.WorkingArea;
        (window.Width, window.Height) = Fit(
            window.Width, window.Height,
            window.MinWidth, window.MinHeight,
            available.Width / screen.Scaling, available.Height / screen.Scaling);

        // AC-1013: A self-measuring window (SizeToContent) gets a MaxHeight ceiling instead of a clamped
        // Height, since it re-measures on every content change; a fixed-height window keeps its Height clamp
        // and stays user-resizable. Details: dropped the clone-dialog/consent-prompt content-change examples.
        if (window.SizeToContent is not SizeToContent.Manual)
        {
            window.MaxHeight = Math.Min(
                window.MaxHeight,
                Math.Max(window.MinHeight, available.Height / screen.Scaling * MaxScreenFraction));
        }
    }

    // Never below the minimums: a dialog too small to use is the failure this is avoiding, not a fix for it.
    internal static (double Width, double Height) Fit(
        double width, double height,
        double minWidth, double minHeight,
        double availableWidth, double availableHeight) =>
        (Math.Clamp(width, minWidth, Math.Max(minWidth, availableWidth * MaxScreenFraction)),
            Math.Clamp(height, minHeight, Math.Max(minHeight, availableHeight * MaxScreenFraction)));
}
