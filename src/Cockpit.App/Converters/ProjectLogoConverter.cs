using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace Cockpit.App.Converters;

// Loads a card logo (AC-162), returning null for a missing file so the initial is shown instead. Cache by path,
// write time and size: replacement logos reuse their project-named path, and refreshes should not re-decode them.
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

    // What the file is now, or null when the path is not one this machine can even ask about.
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
