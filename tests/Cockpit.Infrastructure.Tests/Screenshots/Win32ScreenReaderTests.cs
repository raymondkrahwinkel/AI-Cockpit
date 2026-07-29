using System.Runtime.InteropServices;
using SkiaSharp;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Screenshots;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.Infrastructure.Tests.Screenshots;

/// <summary>
/// The GDI interop itself (AC-327). Everything else about the Windows capture is arithmetic over a faked
/// screen; this is where a wrong struct layout or an upside-down row order shows up — and neither fails loudly.
/// A capture that has gone wrong in either way still returns a perfectly valid PNG of exactly the right size.
/// </summary>
/// <remarks>
/// Runs only on Windows with a desktop attached, so CI (Linux) passes straight over it. That makes it evidence
/// from the machine it ran on rather than a gate, which is exactly what it is worth.
/// <para>
/// The row-order test uses a surface it drew itself. Against the real desktop nothing is provable beyond size
/// and "not a flat colour", because nothing about the content is known — which is why the one bug a human would
/// spot instantly needs eight pixels of its own.
/// </para>
/// </remarks>
public class Win32ScreenReaderTests
{
    [Fact]
    public void TheCaptureIsAPngTheSizeOfTheVirtualScreen()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var reader = new Win32ScreenReader();
        var layout = reader.ReadLayout();

        Assert.True(layout.VirtualBounds.Width > 0);
        Assert.NotEmpty(layout.Displays);

        var png = reader.CapturePng(layout.VirtualBounds);

        Assert.True(PngImage.TryReadSize(png, out var width, out var height), "what GDI produced has to be a readable image");
        Assert.Equal(layout.VirtualBounds.Width, width);
        Assert.Equal(layout.VirtualBounds.Height, height);
    }

    /// <summary>
    /// A desktop is not one colour. The check is deliberately weak — it cannot know what is on the screen — but
    /// a capture that came back as a uniform block is what every one of the silent interop failures produces:
    /// black from an unread bitmap, or a single row smeared down the image.
    /// </summary>
    [Fact]
    public void TheCaptureIsNotAFlatColour()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var reader = new Win32ScreenReader();
        var png = reader.CapturePng(reader.ReadLayout().VirtualBounds);

        // Past the header, a PNG of one flat colour compresses to almost nothing. A real desktop does not.
        Assert.True(png.Length > 20_000);
    }

    /// <summary>
    /// Row order, against a source whose pixels are known. GDI hands back bottom-up rows unless the header's
    /// height is negative, and a capture that came back upside down is a valid PNG of exactly the right size —
    /// nothing about it looks wrong until a human sees the picture. The desktop cannot prove this because
    /// nothing about its content is known; eight pixels drawn here can.
    /// </summary>
    [Fact]
    public void TheTopOfTheSourceIsTheTopOfTheImage()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        using var source = new DrawnSurface(width: 8, height: 8);
        source.FillRows(0, 4, white: true);
        source.FillRows(4, 4, white: false);

        var png = new Win32ScreenReader().CopyFromForTest(source.DeviceContext, new CaptureRect(0, 0, 8, 8));

        using var image = SKBitmap.Decode(png);
        Assert.Equal(255, image.GetPixel(0, 0).Red);
        Assert.Equal(0, image.GetPixel(0, 7).Red);
    }

    /// <summary>Every monitor Windows reports has to sit inside the rectangle that is blitted, or its pixels are not in the image the layout describes.</summary>
    [Fact]
    public void EveryMonitorSitsInsideTheVirtualScreen()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var layout = new Win32ScreenReader().ReadLayout();

        foreach (var display in layout.Displays)
        {
            Assert.True(display.Bounds.X >= layout.VirtualBounds.X);
            Assert.True(display.Bounds.Y >= layout.VirtualBounds.Y);
            Assert.True(display.Bounds.Right <= layout.VirtualBounds.Right);
            Assert.True(display.Bounds.Bottom <= layout.VirtualBounds.Bottom);
            Assert.True(display.Scale >= 1);
        }
    }
}
