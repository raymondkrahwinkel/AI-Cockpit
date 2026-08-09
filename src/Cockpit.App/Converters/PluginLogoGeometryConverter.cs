using System.Globalization;
using System.Xml.Linq;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Platform;

namespace Cockpit.App.Converters;

// Loads a plugin's logo mark (AC-553) as a live vector Geometry rather than a rasterised bitmap, so its Fill
// can bind to the theme's own foreground brush the same way MaterialIcon already does — "vector wins, coloured
// by the app", not a colour baked into a bitmap at build time. Reads the asset's own single <path>, tolerating
// fill-rule="evenodd" (needed for a ring/outline mark, e.g. the magnifier or the archive box). Parsed geometry
// is cached by URI: the asset is a bundled, immutable resource for the process lifetime, so this needs none of
// ProjectLogoConverter's mtime tracking for a file that can be replaced out from under it.
public sealed class PluginLogoGeometryConverter : IValueConverter
{
    public static readonly PluginLogoGeometryConverter Instance = new();

    private readonly Dictionary<string, Geometry?> _cache = new(StringComparer.Ordinal);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string uri || string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        if (_cache.TryGetValue(uri, out var cached))
        {
            return cached;
        }

        var geometry = _Load(uri);
        _cache[uri] = geometry;
        return geometry;
    }

    private static Geometry? _Load(string uri)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            var path = XDocument.Load(stream).Root?.Descendants().FirstOrDefault(element => element.Name.LocalName == "path");
            var data = path?.Attribute("d")?.Value;
            if (string.IsNullOrWhiteSpace(data))
            {
                return null;
            }

            var evenOdd = string.Equals(path?.Attribute("fill-rule")?.Value, "evenodd", StringComparison.OrdinalIgnoreCase);
            return Geometry.Parse((evenOdd ? "F1 " : string.Empty) + data);
        }
        catch (Exception)
        {
            // A malformed asset or one that fails to parse costs the vector mark, not the row — the icon well
            // falls back to the emoji glyph or monogram the same way a plugin with no LogoAsset at all does.
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
