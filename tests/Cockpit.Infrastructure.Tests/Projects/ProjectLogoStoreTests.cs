using Cockpit.Infrastructure.Projects;
using FluentAssertions;
using SkiaSharp;

namespace Cockpit.Infrastructure.Tests.Projects;

/// <summary>
/// Storing a project's logo (AC-162): the cockpit takes its own copy so the card keeps its picture when the source
/// moves, and turns a vector into something the surfaces can actually draw.
/// </summary>
public class ProjectLogoStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cockpit-logo-tests", Guid.NewGuid().ToString("n"));

    private ProjectLogoStore Store() => new(new HttpClient(), logger: null, root: _root);

    private string WriteFile(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Png(int size = 8)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size));
        surface.Canvas.Clear(SKColors.Coral);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public async Task APickedFile_IsCopiedAndItsPathReturned()
    {
        var source = WriteFile("source.png", Png());
        var store = Store();

        var stored = await store.SaveAsync("p1", source);

        stored.Should().NotBeNull();
        File.Exists(stored!).Should().BeTrue();
        stored.Should().NotBe(source, "the point of the copy is that it survives the original going away");
        store.IsStoredCopy(stored).Should().BeTrue();
    }

    [Fact]
    public async Task AnSvg_IsStoredAsThePngItDrawsTo()
    {
        // Every surface that shows a logo takes a decoded bitmap, so a vector has to become one somewhere. Doing it
        // here is what makes a link to a .svg — which is what most logos are — work at all instead of quietly
        // falling back to the project's initial.
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40">
              <rect x="4" y="4" width="32" height="32" fill="#d97757"/>
            </svg>
            """;
        var source = WriteFile("logo.svg", System.Text.Encoding.UTF8.GetBytes(svg));

        var stored = await Store().SaveAsync("p1", source);

        Path.GetExtension(stored).Should().Be(".png");
        using var decoded = SKBitmap.Decode(stored);
        decoded.Should().NotBeNull("a stored logo that no decoder accepts is the same as no logo");
        decoded!.Width.Should().BeGreaterThan(1);
        decoded.GetPixel(decoded.Width / 2, decoded.Height / 2).Alpha.Should().BeGreaterThan(0, "the drawing must actually be on it");
    }

    [Fact]
    public async Task AnSvgWithoutTheExtension_IsStillRecognised()
    {
        // A URL that serves an SVG need not end in .svg — the document itself is the evidence.
        const string svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><circle cx="5" cy="5" r="4" fill="black"/></svg>""";
        var source = WriteFile("logo.img", System.Text.Encoding.UTF8.GetBytes(svg));

        var stored = await Store().SaveAsync("p1", source);

        Path.GetExtension(stored).Should().Be(".png");
        SKBitmap.Decode(stored).Should().NotBeNull();
    }

    [Fact]
    public async Task ASecondLogo_ReplacesTheFirst_SoAProjectOnlyEverHasOne()
    {
        var store = Store();
        await store.SaveAsync("p1", WriteFile("first.png", Png()));

        await store.SaveAsync("p1", WriteFile("second.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 4 4\"><rect width=\"4\" height=\"4\"/></svg>"u8.ToArray()));

        Directory.EnumerateFiles(_root, "p1.*").Should().ContainSingle();
    }

    [Fact]
    public async Task ASourceThatIsNotThere_StoresNothing()
    {
        (await Store().SaveAsync("p1", Path.Combine(_root, "missing.png"))).Should().BeNull();
    }

    [Fact]
    public async Task Remove_TakesTheStoredCopyWhateverKindOfImageItWas()
    {
        var store = Store();
        await store.SaveAsync("p1", WriteFile("source.png", Png()));

        store.Remove("p1");

        Directory.EnumerateFiles(_root, "p1.*").Should().BeEmpty();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
