using SkiaSharp;
using Svg.Skia;

namespace Cockpit.Shared;

// SVG bytes to a decodable PNG: a project logo (AC-162), a plugin store logo (AC-553) and the file preview (AC-730).
// One source file linked into Infrastructure and App, like ChatTurnLoop (AC-1431), so the preview needs no
// Infrastructure reference (AC-1441) and there is still one implementation.
internal static class SvgRasterizer
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
    // does not parse or draws nothing — the try/catch is this method's own, since `SKSvg.Load` throws on
    // malformed XML rather than returning a null/failed result.
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
