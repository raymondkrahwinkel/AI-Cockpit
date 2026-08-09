using System.Globalization;
using System.Xml.Linq;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Platform;

namespace Cockpit.App.Converters;

// A plugin's logo (AC-553) as a live vector Geometry rather than a bitmap, so Fill can bind to the theme's
// foreground brush like MaterialIcon does. Cached by URI — the asset is bundled and immutable for the
// process's lifetime, unlike ProjectLogoConverter's mtime-tracked file.
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
