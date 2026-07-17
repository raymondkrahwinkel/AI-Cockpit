using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.App.Controls;

/// <summary>
/// Builds icons from the bundled Material Design set for code-built controls; the XAML side uses
/// &lt;materialIcons:MaterialIcon&gt; directly. A path drawn by the pack inherits <c>Foreground</c> like any
/// other content, so it reads as part of the theme on every platform — unlike a typed "⚙" glyph, which the
/// machine's emoji font renders in its own colour (on Linux a blue Noto gear in a dark sidebar).
/// </summary>
internal static class CockpitIcons
{
    public static MaterialIcon Icon(MaterialIconKind kind, double size = 14) => new()
    {
        Kind = kind,
        Width = size,
        Height = size,
    };

    public static MaterialIcon Gear(double size = 14) => Icon(MaterialIconKind.Cog, size);
}
