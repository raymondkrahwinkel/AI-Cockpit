using Cockpit.Infrastructure.Projects;
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

        Assert.NotNull(stored);
        Assert.True(File.Exists(stored!));
        Assert.NotEqual(source, stored);
        Assert.True(store.IsStoredCopy(stored));
    }

    [Fact]
    public async Task AnIdThatClimbsOutOfTheFolder_StoresInsideItAnyway()
    {
        // A project id is data from cockpit.json — a hand-written or shared one can hold anything, and it used to
        // decide the file's path. Writing "../../evil.png" put operator data outside the folder the cockpit owns.
        var source = WriteFile("source.png", Png());
        var store = Store();

        var stored = await store.SaveAsync("../../escaped", source);

        Assert.NotNull(stored);
        Assert.StartsWith(Path.GetFullPath(_root), Path.GetFullPath(stored!));
        Assert.True(store.IsStoredCopy(stored));
    }

    [Fact]
    public async Task RemoveWithAWildcardId_LeavesTheOtherProjectsLogosAlone()
    {
        // Remove used to hand the id to a search pattern, so an id of "*" — or one climbing into another folder —
        // matched, and deleted, files that were never this project's.
        var store = Store();
        await store.SaveAsync("keep-me", WriteFile("a.png", Png()));

        store.Remove("*");

        Assert.Single(Directory.EnumerateFiles(_root, "keep-me.*"));
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
