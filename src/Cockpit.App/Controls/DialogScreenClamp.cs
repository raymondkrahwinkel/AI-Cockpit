using Avalonia.Controls;

namespace Cockpit.App.Controls;

/// <summary>
/// Shrinks a dialog to fit its screen when its designed size does not. Dialogs sized for a desktop (the
/// plugin store's catalogue grid, the manage-profiles form) are larger than a small screen; a fixed size
/// larger than the screen is not a bigger dialog — it is one whose buttons are past the bottom edge,
/// centred on its owner with nothing to drag it back by.
/// </summary>
internal static class DialogScreenClamp
{
    /// <summary>How much of the screen's working area the dialog may take when its designed size does not fit.</summary>
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

        // A window that measures itself has no height for the line above to clamp, and measures itself again
        // every time its content changes — a git error arriving in the clone dialog, a plugin's install path in
        // the consent prompt. It gets a ceiling instead, which outlives this call. A window with a height of its
        // own does not: the operator can still drag it larger than the screen, as they always could, and taking
        // that away is not what fitting on the screen means.
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
