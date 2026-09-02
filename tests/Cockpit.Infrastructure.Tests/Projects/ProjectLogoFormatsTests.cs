using Cockpit.Core.Abstractions.Projects;
using Cockpit.Infrastructure.Projects;
using SkiaSharp;

namespace Cockpit.Infrastructure.Tests.Projects;

/// <summary>
/// The one list of formats a logo may come in (AC-373). The store and the file picker each used to keep their own,
/// and they drifted: the store had learned to rasterise an SVG while the picker still refused to show the operator
/// one. These tests hold the list and the store to each other — the picker builds its filter straight from the list,
/// so an extension that survives a round trip here is one the operator can actually pick.
/// </summary>
/// <remarks>
/// Asserted with xunit's own Assert rather than the fluent library its neighbours use: that one is on its way out
/// (AC-372), and its own file rather than a mixed one so nothing new has to be swept.
/// </remarks>
public class ProjectLogoFormatsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cockpit-logo-format-tests", Guid.NewGuid().ToString("n"));

    [Fact]
    public void TheListOffersAVector_BecauseAlogoUsuallyIsOne()
        => Assert.Contains(".svg", ProjectLogoFormats.Extensions);

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".webp")]
    [InlineData(".gif")]
    [InlineData(".bmp")]
    [InlineData(".ico")]
    public async Task EveryListedRasterFormat_IsStoredUnderItsOwnExtension(string extension)
    {
        // Falling back to .png would still store the bytes, so the round trip is what shows the store recognised the
        // format rather than merely tolerating it.
        Assert.Contains(extension, ProjectLogoFormats.Extensions);
        var source = _Write($"source{extension}", _Png());

        var stored = await new ProjectLogoStore(new HttpClient(), logger: null, root: _root).SaveAsync("p1", source);

        Assert.NotNull(stored);
        Assert.Equal(extension, Path.GetExtension(stored));
    }

    private string _Write(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);

        return path;
    }

    private static byte[] _Png()
    {
        using var surface = SKSurface.Create(new SKImageInfo(8, 8));
        surface.Canvas.Clear(SKColors.Coral);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        return data.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
