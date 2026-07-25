using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace Cockpit.App.Converters;

/// <summary>
/// Loads a project's stored logo for its card (AC-162). Returns <see langword="null"/> for a project without one,
/// or one whose file has gone, so the card falls back to its initial rather than showing a broken image.
/// </summary>
/// <remarks>
/// Decoded once per path and kept: the overview rebinds its cards on every refresh, and re-reading the same handful
/// of small images from disk each time is work nobody asked for. The cache is keyed on the path, which is stable —
/// a replaced logo is written under a new name (the extension follows the source), so a stale entry cannot win.
/// </remarks>
public sealed class ProjectLogoConverter : IValueConverter
{
    public static readonly ProjectLogoConverter Instance = new();

    private readonly Dictionary<string, Bitmap?> _decoded = new(StringComparer.Ordinal);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (_decoded.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var bitmap = _Decode(path);
        _decoded[path] = bitmap;
        return bitmap;
    }

    private static Bitmap? _Decode(string path)
    {
        try
        {
            return File.Exists(path) ? new Bitmap(path) : null;
        }
        catch (Exception)
        {
            // Not an image, or one this platform cannot decode. The card shows its initial; a project is not worth
            // less for having a logo that turned out to be a text file.
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
