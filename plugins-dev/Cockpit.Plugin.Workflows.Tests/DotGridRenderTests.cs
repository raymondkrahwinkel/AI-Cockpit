using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Cockpit.Plugin.Workflows.Canvas;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// <see cref="DotGrid"/> paints its dots out of a <see cref="DrawingBrush"/> handed to
/// <c>WorkflowCanvas.Background</c>. The AC-338 palette harness reads brushes off properties, and a
/// <see cref="DrawingBrush"/> is one property holding a whole nested drawing rather than a colour — so the grid's
/// own doc comment calls it "the one part of the canvas that never followed the repaint at all", and that fault
/// would still be invisible to every baseline in the repository (AC-413). Rendering it and reading the frame back
/// is the only way to know what colour actually reached the screen.
/// </summary>
[Collection("avalonia")]
public class DotGridRenderTests
{
    /// <summary>
    /// A dot is <see cref="DotGrid"/>'s 1.6px across, which antialiasing leaves fully opaque nowhere — unscaled, no
    /// pixel carries the token itself and the assertion below could only be a "close enough" against a hand-picked
    /// tolerance. Magnified, the dot's middle is covered outright, which is what lets it be an equality instead.
    /// </summary>
    private const int Magnification = 8;

    private const int SurfaceSize = 64;

    [Fact]
    public void TheDots_AreDrawnInTheThemesHairlineColour_NotALiteral()
    {
        using var host = _Rendered();
        var painted = _PaintedColours(host.Window);

        Assert.Contains(_AsRendered(_Token("CockpitHairlineBrush")), painted);
    }

    [Fact]
    public void TheDots_AreDrawnAtAll_RatherThanLeavingTheSurfaceBare()
    {
        // The assertion above passes vacuously the day the hairline token becomes the backdrop's own colour, and it
        // would also pass on a surface that drew one dot in the corner. This says the grid covered the surface.
        using var host = _Rendered();

        var painted = _PaintedColours(host.Window);

        Assert.True(painted.Count > 1, "a surface carrying a dot grid has more than the backdrop's one colour on it");
    }

    /// <summary>The dot-grid brush on a bare surface, magnified, shown and laid out.</summary>
    private static Host _Rendered()
    {
        var surface = new Border
        {
            Width = SurfaceSize,
            Height = SurfaceSize,
            Background = DotGrid.Build(),
            RenderTransform = new ScaleTransform(Magnification, Magnification),
            RenderTransformOrigin = RelativePoint.TopLeft,
        };

        var window = new Window
        {
            Width = SurfaceSize * Magnification,
            Height = SurfaceSize * Magnification,
            Content = surface,
        };
        window.Show();
        window.UpdateLayout();

        return new Host(window);
    }

    /// <summary>
    /// Every distinct colour in the frame. Scanning rather than sampling a computed dot centre: a
    /// <see cref="DrawingBrush"/> aligns its tiles to the destination rect, so where in the surface a dot lands is
    /// the brush's business, not something a test should have to predict.
    /// <para>
    /// The host's <c>RenderedScene</c> reads a frame the same way and the two are not shared, which
    /// <c>Cockpit.TestSupport</c> would normally be the answer to. It is not here: capturing a frame needs
    /// <c>Avalonia.Headless</c>, that project deliberately carries no rendering runtime ("each caller brings the
    /// runtime it shows it with"), and putting one there would hand it to all eight of its consumers — six of which
    /// render nothing — to spare six lines.
    /// </para>
    /// </summary>
    private static HashSet<IReadOnlyList<byte>> _PaintedColours(Window window)
    {
        using var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("the headless renderer produced no frame to sample");
        using var buffer = frame.Lock();

        var bytesPerPixel = buffer.RowBytes / buffer.Size.Width;
        var row = new byte[buffer.RowBytes];
        var colours = new HashSet<IReadOnlyList<byte>>(ChannelComparer);

        for (var y = 0; y < buffer.Size.Height; y++)
        {
            Marshal.Copy(buffer.Address + (y * buffer.RowBytes), row, 0, row.Length);

            for (var x = 0; x < buffer.Size.Width; x++)
            {
                var offset = x * bytesPerPixel;
                colours.Add(new[] { row[offset], row[offset + 1], row[offset + 2] });
            }
        }

        return colours;
    }

    private static readonly IEqualityComparer<IReadOnlyList<byte>> ChannelComparer =
        EqualityComparer<IReadOnlyList<byte>>.Create(
            (left, right) => left is not null && right is not null && left.SequenceEqual(right),
            channels => HashCode.Combine(channels[0], channels[1], channels[2]));

    /// <summary>
    /// What the token looks like once it has been through the same renderer: a plain fill of it, read back off a
    /// frame. Comparing rendered bytes against rendered bytes rather than against the token's own R/G/B sidesteps
    /// the frame buffer's channel order being the platform's business (BGRA on one machine, RGBA on another)
    /// without having to sort the channels — and sorting would make a colour indistinguishable from its own
    /// permutations, so a dot that came out <c>#392f2a</c> instead of <c>#2a2f39</c> would still pass.
    /// </summary>
    private static IReadOnlyList<byte> _AsRendered(IBrush brush)
    {
        var window = new Window { Width = 16, Height = 16, Content = new Border { Background = brush } };
        window.Show();
        window.UpdateLayout();

        try
        {
            return _PaintedColours(window).Single();
        }
        finally
        {
            window.Close();
        }
    }

    private static IBrush _Token(string key) =>
        Application.Current?.FindResource(key) as IBrush ?? throw new InvalidOperationException($"no brush token '{key}'");

    private sealed record Host(Window Window) : IDisposable
    {
        public void Dispose() => Window.Close();
    }
}
