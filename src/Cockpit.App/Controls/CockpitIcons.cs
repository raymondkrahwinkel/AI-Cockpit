using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.App.Controls;

// AC-1013: Builds icons from the bundled Material Design set (not a typed glyph) so they inherit
// `Foreground` and stay theme-consistent, instead of rendering in the platform's own emoji-font
// colour (e.g. a blue Noto gear on Linux). Details: dropped the concrete Linux/dark-sidebar example.
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
