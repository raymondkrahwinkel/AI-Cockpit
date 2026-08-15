using SkiaSharp;

namespace Cockpit.Plugin.Diagram.Tests;

public class DiagramExportTests
{
    private const string Square = """<svg xmlns="http://www.w3.org/2000/svg" width="10" height="20"><rect x="2" y="2" width="6" height="16" fill="red"/></svg>""";

    [Fact]
    public void RasterizePng_scales_the_native_svg_size()
    {
        var png = DiagramExport.RasterizePng(Square, scale: 3f, transparent: true);

        Assert.NotNull(png);
        using var bitmap = SKBitmap.Decode(png);
        Assert.Equal(30, bitmap.Width);
        Assert.Equal(60, bitmap.Height);
    }

    [Fact]
    public void RasterizePng_transparent_true_keeps_background_alpha_zero()
    {
        var png = DiagramExport.RasterizePng(Square, scale: 1f, transparent: true);

        using var bitmap = SKBitmap.Decode(png);
        Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public void RasterizePng_transparent_false_fills_white_background()
    {
        var png = DiagramExport.RasterizePng(Square, scale: 1f, transparent: false);

        using var bitmap = SKBitmap.Decode(png);
        Assert.Equal(new SKColor(255, 255, 255, 255), bitmap.GetPixel(0, 0));
    }

    [Fact]
    public void RasterizePng_returns_null_for_unparseable_markup()
    {
        Assert.Null(DiagramExport.RasterizePng("not svg", scale: 1f, transparent: true));
    }
}
