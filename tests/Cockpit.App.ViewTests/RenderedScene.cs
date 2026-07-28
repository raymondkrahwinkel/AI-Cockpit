using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;

namespace Cockpit.App.ViewTests;

/// <summary>
/// Showing a control and reading colours back out of the frame it produced. Two families of test need this — the
/// template parts AC-336 reclaimed from Fluent, and the controls that paint in a <c>Render</c> override and put
/// nothing on a property at all (AC-413) — so it lives here rather than as a second copy in the newer one.
/// </summary>
internal static class RenderedScene
{
    /// <summary>The control in a window of its own, laid out and drawn, closed when the caller is done with it.</summary>
    public static Scene Show(Control content, double width = 400, double height = 300)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        window.UpdateLayout();

        return new Scene(window);
    }

    /// <summary>The live value of a <c>Theme.axaml</c> colour token, so an assertion follows a repaint instead of pinning a hex.</summary>
    public static Color Token(string key) =>
        (Color)(Application.Current?.FindResource(key) ?? throw new InvalidOperationException($"no token '{key}'"));

    /// <summary>The live brush behind a <c>Theme.axaml</c> token, for comparing against something rendered.</summary>
    public static IBrush TokenBrush(string key) =>
        Application.Current?.FindResource(key) as IBrush ?? throw new InvalidOperationException($"no brush token '{key}'");

    /// <summary>
    /// What a brush puts on screen, read back through the same renderer as <see cref="PaintedAt"/>. Compare a
    /// rendered pixel against this rather than against a token's own R/G/B: the frame buffer's channel order is
    /// the platform's business (BGRA on one machine, RGBA on another), and putting both sides through the same
    /// path settles that without sorting the channels — sorting would make a colour equal to its own permutations,
    /// so ink that came out <c>#392f2a</c> where the token is <c>#2a2f39</c> would pass.
    /// </summary>
    public static Color AsRendered(IBrush brush)
    {
        using var scene = Show(new Border { Background = brush }, width: 16, height: 16);

        return PaintedAt(scene.Window, new Point(8, 8));
    }

    /// <summary>
    /// The colour actually rendered at a point of the window, read back out of the frame. Its three channels are
    /// in the buffer's own order rather than R/G/B, so it is only meaningful against another rendered colour.
    /// </summary>
    public static Color PaintedAt(Window window, Point point)
    {
        using var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("the headless renderer produced no frame to sample");
        using var buffer = frame.Lock();

        var bytesPerPixel = buffer.RowBytes / buffer.Size.Width;
        var row = new byte[buffer.RowBytes];
        Marshal.Copy(buffer.Address + ((int)point.Y * buffer.RowBytes), row, 0, row.Length);

        var offset = (int)point.X * bytesPerPixel;
        return Color.FromRgb(row[offset], row[offset + 1], row[offset + 2]);
    }

    public sealed record Scene(Window Window) : IDisposable
    {
        public void Dispose() => Window.Close();
    }
}
