using System.Globalization;
using Avalonia.Media.Imaging;
using Cockpit.App.Converters;
using SkiaSharp;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The converter that puts a project's stored logo on its card (AC-373). Its cache is the whole risk: the store
/// names a logo after its project, so replacing one with a file of the same kind writes the same path, and a cache
/// that keys on the path alone hands back the picture the operator just replaced.
/// </summary>
/// <remarks>
/// Asserted with xunit's own Assert rather than the fluent library the rest of this project still uses: that one is
/// on its way out (AC-372) and nothing new should have to be swept out again.
/// </remarks>
[Collection("avalonia")]
public class ProjectLogoConverterTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("cockpit-logo-tests").FullName;

    [Fact]
    public void ANewLogoAtTheSamePath_IsShownInsteadOfTheOldOne() => HeadlessAvalonia.Run(() =>
    {
        var converter = new ProjectLogoConverter();
        var path = Path.Combine(_folder, "project-a.png");
        File.WriteAllBytes(path, _Png(8, 8));

        var before = _Convert(converter, path);
        File.WriteAllBytes(path, _Png(32, 24));
        var after = _Convert(converter, path);

        Assert.Equal(8, before?.PixelSize.Width);
        Assert.Equal(32, after?.PixelSize.Width);
    });

    [Fact]
    public void ALogoRemovedAndThenReplaced_IsShownAsTheNewOne() => HeadlessAvalonia.Run(() =>
    {
        // The order the operator reported: clear the logo, save, pick another, save. The store deletes and then
        // writes the same name back, so the path is identical on both sides of the gap.
        var converter = new ProjectLogoConverter();
        var path = Path.Combine(_folder, "project-d.png");
        File.WriteAllBytes(path, _Png(8, 8));
        _Convert(converter, path);

        File.Delete(path);
        Assert.Null(_Convert(converter, path));

        File.WriteAllBytes(path, _Png(48, 16));

        Assert.Equal(48, _Convert(converter, path)?.PixelSize.Width);
    });

    [Fact]
    public void AnUnchangedLogo_IsDecodedOnlyOnce() => HeadlessAvalonia.Run(() =>
    {
        var converter = new ProjectLogoConverter();
        var path = Path.Combine(_folder, "project-b.png");
        File.WriteAllBytes(path, _Png(8, 8));

        var first = _Convert(converter, path);
        var second = _Convert(converter, path);

        Assert.Same(first, second);
    });

    [Fact]
    public void ARemovedLogo_StopsBeingShown() => HeadlessAvalonia.Run(() =>
    {
        var converter = new ProjectLogoConverter();
        var path = Path.Combine(_folder, "project-c.png");
        File.WriteAllBytes(path, _Png(8, 8));

        Assert.NotNull(_Convert(converter, path));
        File.Delete(path);

        Assert.Null(_Convert(converter, path));
    });

    [Fact]
    public void APathThatIsNotAFile_LeavesTheCardOnItsInitial() => HeadlessAvalonia.Run(() =>
    {
        var converter = new ProjectLogoConverter();

        Assert.Null(_Convert(converter, "\0not-a-path"));
        Assert.Null(_Convert(converter, Path.Combine(_folder, "never-written.png")));
    });

    private static Bitmap? _Convert(ProjectLogoConverter converter, string path) =>
        converter.Convert(path, typeof(Bitmap), null, CultureInfo.InvariantCulture) as Bitmap;

    /// <summary>A real PNG of the given size — the point is that two of them differ in both content and length.</summary>
    private static byte[] _Png(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(new SKColor((byte)width, (byte)height, 0x40));

        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        return encoded.ToArray();
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
