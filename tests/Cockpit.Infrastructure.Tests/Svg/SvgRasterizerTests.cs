using System.Text;
using Cockpit.Infrastructure.Svg;

namespace Cockpit.Infrastructure.Tests.Svg;

/// <summary>
/// The shared Skia-backed SVG handling extracted for AC-553 (a provider plugin's vendor CDN logo) out of
/// ProjectLogoStore's own private methods (AC-162) — covered directly here rather than only indirectly
/// through ProjectLogoStoreTests, since it is now a shared, public surface.
/// </summary>
public class SvgRasterizerTests
{
    private const string SquareSvg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><rect width="10" height="10" fill="red"/></svg>""";

    [Fact]
    public void LooksLikeSvg_SniffsTheDocumentEvenWithoutAnSvgExtension()
    {
        var bytes = Encoding.UTF8.GetBytes(SquareSvg);

        Assert.True(SvgRasterizer.LooksLikeSvg(bytes));
        Assert.True(SvgRasterizer.LooksLikeSvg([0x89, 0x50, 0x4E, 0x47], extension: ".svg"));
        Assert.False(SvgRasterizer.LooksLikeSvg([0x89, 0x50, 0x4E, 0x47]));
    }

    [Fact]
    public void Rasterize_DrawsTheSvgOntoAPngAtTheRequestedSize()
    {
        var bytes = Encoding.UTF8.GetBytes(SquareSvg);

        var png = SvgRasterizer.Rasterize(bytes, 64f);

        Assert.NotNull(png);
        // A PNG's own magic number — proof this is a decodable raster image, not a pass-through of the SVG text.
        Assert.Equal(0x89, png![0]);
        Assert.Equal((byte)'P', png[1]);
        Assert.Equal((byte)'N', png[2]);
        Assert.Equal((byte)'G', png[3]);
    }

    [Fact]
    public void Rasterize_OnUnparsableBytes_ReturnsNull_RatherThanThrowing()
    {
        var result = SvgRasterizer.Rasterize("not an svg document"u8.ToArray(), 64f);

        Assert.Null(result);
    }
}
