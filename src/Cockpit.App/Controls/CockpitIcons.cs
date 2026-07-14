using Avalonia.Controls;
using Avalonia.Media;

namespace Cockpit.App.Controls;

/// <summary>
/// The cockpit's own glyphs, drawn rather than typed. A gear written as the character "⚙" is rendered by whatever
/// emoji font the machine happens to have — on Linux that is a blue Noto Color Emoji gear, which ignores the theme
/// and lands in a dark sidebar as a splash of someone else's colour. A path inherits <see cref="Control.Foreground"/>
/// like any other content, so it reads as part of the app on every platform.
/// </summary>
internal static class CockpitIcons
{
    // Material Design "settings" (24×24), the shape a gear icon is expected to have.
    private const string GearPath =
        "M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58a.49.49 0 0 0 .12-.61l-1.92-3.32a.49.49 0 0 0-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54a.48.48 0 0 0-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96a.47.47 0 0 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 1.58a.49.49 0 0 0-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32a.49.49 0 0 0-.12-.61l-2.01-1.58zM12 15.6a3.6 3.6 0 1 1 0-7.2 3.6 3.6 0 0 1 0 7.2z";

    public static Control Gear(double size = 14) => new PathIcon
    {
        Data = Geometry.Parse(GearPath),
        Width = size,
        Height = size,
    };
}
