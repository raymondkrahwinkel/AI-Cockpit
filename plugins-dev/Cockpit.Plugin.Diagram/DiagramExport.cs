using SkiaSharp;
using Svg.Skia;

namespace Cockpit.Plugin.Diagram;

// SVG->PNG for the export button (AC-813). Same shape as Cockpit.Infrastructure's SvgRasterizer, kept local
// rather than referenced: that assembly drags in Whisper/Velopack/PDFium/etc, far past what one button needs.
// This plugin already carries Svg.Skia/SkiaSharp compile-only for the viewer control (AC-809) — same fallthrough.
internal static class DiagramExport
{
    public static byte[]? RasterizePng(string svgMarkup, float scale, bool transparent)
    {
        try
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(svgMarkup));
            using var svg = new SKSvg();
            if (svg.Load(stream) is not { } picture || picture.CullRect is { Width: <= 0 } or { Height: <= 0 })
            {
                return null;
            }

            var source = picture.CullRect;
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));

            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            surface.Canvas.Clear(transparent ? SKColors.Transparent : SKColors.White);
            surface.Canvas.Scale(scale);
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
