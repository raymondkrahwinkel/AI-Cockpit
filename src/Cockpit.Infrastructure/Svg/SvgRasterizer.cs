using SkiaSharp;
using Svg.Skia;

namespace Cockpit.Infrastructure.Svg;

// Shared Skia-backed SVG handling — ProjectLogoStore (AC-162, a user-picked project logo) and
// PluginManagerViewModel (AC-553, a provider plugin's vendor CDN logo) both need to turn SVG bytes into a
// decodable bitmap; one implementation here rather than two copies drifting apart.
public static class SvgRasterizer
{
    // Whether these bytes are an SVG: by extension, or by what the document actually starts with — a URL that serves one need not end in `.svg`.
    public static bool LooksLikeSvg(byte[] bytes, string? extension = null)
    {
        if (string.Equals(extension, ".svg", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var start = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512));
        return start.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    // The SVG drawn onto a PNG at `maxSize` on its longest side, transparent behind it. Null when the document
    // does not parse or draws nothing, which leaves the caller on its own fallback rather than an empty square.
    // The try/catch is this method's own: `SKSvg.Load` throws (an XmlException, not a null/failed result) on
    // bytes that merely look like an SVG by content-sniffing but are not well-formed XML — a caller downloading
    // arbitrary bytes off a URL must be able to trust the "null on a bad document" contract this doc comment
    // already promises, rather than every caller needing its own try/catch to make that true.
    public static byte[]? Rasterize(byte[] bytes, float maxSize)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            using var svg = new SKSvg();
            if (svg.Load(stream) is not { } picture || picture.CullRect is { Width: <= 0 } or { Height: <= 0 })
            {
                return null;
            }

            var source = picture.CullRect;
            var scale = maxSize / Math.Max(source.Width, source.Height);
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));

            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Scale(scale);

            // Drawn from the picture's own origin: an SVG whose contents start away from (0,0) would otherwise be
            // rendered partly outside the surface.
            surface.Canvas.Translate(-source.Left, -source.Top);
            surface.Canvas.DrawPicture(picture);
            surface.Canvas.Flush();

            using var image = surface.Snapshot();
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return encoded?.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
