using Avalonia.Controls;
using Avalonia.Input;
using Cockpit.App.Services;
using FluentAssertions;
using SkiaSharp;

namespace Cockpit.App.ViewTests;

/// <summary>
/// What the capture write leaves on the clipboard (AC-341): an image, under the format an image is asked for.
/// </summary>
/// <remarks>
/// This is the shape guard, not the flush guard — Avalonia's headless clipboard is a plain in-memory store and
/// stays green with the flush deleted, which was checked. <c>ScreenshotClipboardFlushGuardTests</c> holds that
/// half.
/// <para>
/// What it does hold: writing the PNG <em>bytes</em> instead of a bitmap is the obvious-looking fix here, and it
/// does not work. Measured on Windows on 2026-07-27, and confirmed in Avalonia 12.0.5's source: the identifiers
/// <c>PNG</c>, <c>image/png</c>, <c>CF_DIB</c>, <c>CF_DIBV5</c> and <c>CF_BITMAP</c> are claimed process-wide as
/// <c>DataFormat&lt;Bitmap&gt;</c>, and since <c>DataFormat</c> compares on kind and identifier alone, a
/// <c>DataFormat&lt;byte[]&gt;</c> under one of those names is the same key — the bytes are dropped. macOS folds
/// <c>public.png</c> into the same bitmap format. Only X11 treats it as bytes, and there Avalonia already writes
/// <c>image/png</c> when you set a bitmap. So the bitmap is the portable route, and this fails if it goes.
/// </para>
/// </remarks>
[Collection("avalonia")]
public class ScreenshotClipboardWriteTests
{
    [Fact]
    public void ACaptureOnTheClipboard_IsThereAsAnImage() => HeadlessAvalonia.Run(() =>
    {
        var window = new Window();
        window.Show();
        var clipboard = window.Clipboard;
        clipboard.Should().NotBeNull();

        var landed = AvaloniaScreenshotClipboard.WriteAsync(clipboard, _Png()).GetAwaiter().GetResult();

        landed.Should().BeTrue();
        using var onClipboard = clipboard.TryGetDataAsync().GetAwaiter().GetResult();
        onClipboard.Should().NotBeNull();
        onClipboard.Contains(DataFormat.Bitmap).Should().BeTrue(
            "the terminal asks the clipboard for an image; bytes under a format name Avalonia reserves for bitmaps are dropped");
    });

    /// <summary>
    /// A capture that will not decode is not an image to hand on, and the caller has to hear that: the paste key
    /// would otherwise ask the TUI to paste something that is not there.
    /// </summary>
    [Fact]
    public void ACaptureThatWillNotDecode_IsReportedAsNotLanded() => HeadlessAvalonia.Run(() =>
    {
        var window = new Window();
        window.Show();
        var clipboard = window.Clipboard;
        clipboard.Should().NotBeNull();

        var landed = AvaloniaScreenshotClipboard.WriteAsync(clipboard, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]).GetAwaiter().GetResult();

        landed.Should().BeFalse();
    });

    private static byte[] _Png()
    {
        using var surface = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var image = SKImage.FromBitmap(surface);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        return encoded.ToArray();
    }
}
