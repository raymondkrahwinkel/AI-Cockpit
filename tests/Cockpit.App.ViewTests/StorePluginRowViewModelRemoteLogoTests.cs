using Avalonia.Media.Imaging;
using SkiaSharp;
using Cockpit.App.ViewModels;
using Cockpit.Core.Plugins;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-553 option A: once a vendor CDN logo actually decodes (PluginManagerViewModel's async fetch landing
/// <see cref="StorePluginRowViewModel.RemoteLogo"/>), it must win over the glyph/monogram tiers it was
/// standing in for — the "still waiting" half of this fallback is covered without Avalonia in
/// Cockpit.Core.Tests' StorePluginRowViewModelTests; this is the half that needs a real decoded Bitmap.
/// </summary>
[Collection("avalonia")]
public class StorePluginRowViewModelRemoteLogoTests
{
    [Fact]
    public void SettingRemoteLogo_StepsAsideTheGlyphAndMonogramTiers() => HeadlessAvalonia.Run(() =>
    {
        var entry = new PluginStoreEntry(
            Id: "claude-provider", Name: "Claude Code", Description: "d", Author: "Cockpit",
            LatestVersion: "1.0.0", Versions: [], Icon: "🌙", LogoAsset: "https://claude.ai/favicon.svg");
        var row = new StorePluginRowViewModel(entry, PluginStoreConfig.Remote("url"), null);

        Assert.True(row.IsRemoteLogoAsset);
        Assert.False(row.HasRemoteLogo);
        Assert.True(row.ShowsIconGlyph);

        row.RemoteLogo = _Bitmap(24, 24);

        Assert.True(row.HasRemoteLogo);
        Assert.False(row.ShowsIconGlyph);
        Assert.False(row.ShowsMonogram);
    });

    private static Bitmap _Bitmap(int width, int height)
    {
        using var surface = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var image = SKImage.FromBitmap(surface);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(encoded.ToArray());

        return new Bitmap(stream);
    }
}
