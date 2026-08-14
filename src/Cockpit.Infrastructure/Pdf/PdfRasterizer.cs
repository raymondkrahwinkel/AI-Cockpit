using PDFtoImage;
using SkiaSharp;

namespace Cockpit.Infrastructure.Pdf;

// Rasterises page 1 of a PDF for FilePreviewWindow's preview (AC-730) — mirrors SvgRasterizer.Rasterize:
// PDFium (native, the renderer Chrome ships) + SkiaSharp turn PDF bytes into a decodable PNG. Page 1 only in
// this round; browsing further pages is a second design question (paging, memory on a 400-page PDF) and does
// not belong in the same round as this one — PageCount is returned so the caller can at least show the total.
public static class PdfRasterizer
{
    // Matches SvgRasterizer's SvgRasterSize cap, so an image/SVG/PDF preview scales similarly either way.
    private const int MaxWidth = 1600;

    // Png is null (with a human-readable Error) when the document does not open — encrypted, corrupt, or
    // otherwise unreadable.
    public sealed record Result(byte[]? Png, int PageCount, string? Error);

    public static Result Rasterize(byte[] bytes)
    {
        try
        {
            // CA1416: Conversion's [SupportedOSPlatform] list is Windows/Linux/macOS/Android/iOS/browser — every
            // desktop platform Cockpit ships on is already in it, so this is not an actual platform gap.
#pragma warning disable CA1416
            var pageCount = Conversion.GetPageCount(bytes);
            using var bitmap = Conversion.ToImage(bytes, 0, options: new RenderOptions(Width: MaxWidth, WithAspectRatio: true));
#pragma warning restore CA1416
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return new Result(encoded.ToArray(), pageCount, null);
        }
        catch (Exception ex)
        {
            return new Result(null, 0, ex.Message);
        }
    }
}
