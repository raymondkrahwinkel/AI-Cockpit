using System.Globalization;
using System.Text;
using Avalonia.Media.Imaging;
using Cockpit.App.Converters;
using Cockpit.Infrastructure.Projects;
using SkiaSharp;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The whole way a project's logo travels (AC-373): the store takes its own copy, and the card decodes whatever the
/// store left behind. Both halves are covered on their own; this is the order that was actually reported — clear the
/// logo, save, pick another, save — and it is the seam between them that put the old picture back, because the store
/// hands out the same path twice and the card used to take that as proof the picture had not changed.
/// </summary>
/// <remarks>
/// Asserted with xunit's own Assert: the fluent library its neighbours use is on its way out (AC-372).
/// </remarks>
[Collection("avalonia")]
public class ProjectLogoEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cockpit-logo-e2e", Guid.NewGuid().ToString("n"));

    [Fact]
    public async Task ClearingALogoAndPickingAnother_PutsTheNewPictureOnTheCard()
    {
        var store = new ProjectLogoStore(new HttpClient(), logger: null, root: _root);
        var converter = new ProjectLogoConverter();

        var first = await store.SaveAsync("acme", _Source("first.png", _Png(8)));
        Assert.NotNull(first);
        HeadlessAvalonia.Run(() => Assert.Equal(8, _Convert(converter, first).PixelSize.Width));

        store.Remove("acme");
        var second = await store.SaveAsync("acme", _Source("second.png", _Png(32)));
        Assert.NotNull(second);

        // The premise of the whole ticket: the store gives back the path it gave the first time.
        Assert.Equal(first, second);
        HeadlessAvalonia.Run(() => Assert.Equal(32, _Convert(converter, second).PixelSize.Width));
    }

    [Fact]
    public async Task AVectorLogo_ReachesTheCardAsSomethingItCanDraw()
    {
        // What picking an .svg has to end in. The store rasterises it; the card has no idea it was ever a vector.
        const string svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40"><rect width="40" height="40" fill="black"/></svg>""";
        var store = new ProjectLogoStore(new HttpClient(), logger: null, root: _root);
        var converter = new ProjectLogoConverter();

        var stored = await store.SaveAsync("acme", _Source("logo.svg", Encoding.UTF8.GetBytes(svg)));

        Assert.NotNull(stored);
        HeadlessAvalonia.Run(() => Assert.True(_Convert(converter, stored).PixelSize.Width > 0));
    }

    private static Bitmap _Convert(ProjectLogoConverter converter, string path)
    {
        var bitmap = converter.Convert(path, typeof(Bitmap), null, CultureInfo.InvariantCulture) as Bitmap;

        return bitmap ?? throw new InvalidOperationException($"The card was left without a picture for {path}.");
    }

    private string _Source(string name, byte[] bytes)
    {
        var folder = Path.Combine(_root, "picked");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);

        return path;
    }

    private static byte[] _Png(int size)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(new SKColor((byte)size, 0x20, 0x40));

        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        return encoded.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
