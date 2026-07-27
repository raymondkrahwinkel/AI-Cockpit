using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace Cockpit.App.Converters;

/// <summary>
/// Loads a project's stored logo for its card (AC-162). Returns <see langword="null"/> for a project without one,
/// or one whose file has gone, so the card falls back to its initial rather than showing a broken image.
/// </summary>
/// <remarks>
/// Decoded once and kept: the overview rebinds its cards on every refresh, and re-reading the same handful of small
/// images from disk each time is work nobody asked for. The path alone will not do as the key — the store names a
/// logo after its project, so replacing one with a file of the same kind reuses the path exactly, and a cache that
/// trusts the name serves the picture the operator just threw away. The write time and the size decide instead;
/// they are a stat, where decoding is a read plus the decode.
/// </remarks>
public sealed class ProjectLogoConverter : IValueConverter
{
    public static readonly ProjectLogoConverter Instance = new();

    private readonly Dictionary<string, (DateTime WrittenUtc, long Length, Bitmap? Bitmap)> _decoded = new(StringComparer.Ordinal);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (_Stat(path) is not { Exists: true } file)
        {
            _decoded.Remove(path);
            return null;
        }

        if (_decoded.TryGetValue(path, out var cached) && cached.WrittenUtc == file.LastWriteTimeUtc && cached.Length == file.Length)
        {
            return cached.Bitmap;
        }

        // The replaced bitmap is not disposed: an Image built from it may still be on screen while this runs, and a
        // disposed one renders as a crash rather than as a stale picture. A handful of small images that outlive
        // their card until the process ends is the cheaper of the two.
        var bitmap = _Decode(path);
        _decoded[path] = (file.LastWriteTimeUtc, file.Length, bitmap);
        return bitmap;
    }

    /// <summary>What the file is now, or null when the path is not one this machine can even ask about.</summary>
    private static FileInfo? _Stat(string path)
    {
        try
        {
            return new FileInfo(path);
        }
        catch (Exception)
        {
            // A logo path comes out of cockpit.json and can be anything a hand edit put there. Refusing to draw a
            // card over it would cost the project, which is the same trade _Decode makes for a file that is there
            // but is not an image.
            return null;
        }
    }

    private static Bitmap? _Decode(string path)
    {
        try
        {
            return new Bitmap(path);
        }
        catch (Exception)
        {
            // Not an image, one this platform cannot decode, or a file that went away between the stat and the
            // read. The card shows its initial; a project is not worth less for having a logo that turned out to
            // be a text file.
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
