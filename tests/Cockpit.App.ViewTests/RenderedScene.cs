using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Cockpit.TestSupport;

namespace Cockpit.App.ViewTests;

// Shared by the AC-336 template-part tests and the AC-413 Render-override tests, so it lives here and not as a second copy.
internal static class RenderedScene
{
    public static Scene Show(Control content, double width = 400, double height = 300)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        window.UpdateLayout();

        return new Scene(window);
    }

    public static Color Token(string key) => ThemeTokens.Colour(key);

    public static IBrush TokenBrush(string key) =>
        Application.Current?.FindResource(key) as IBrush ?? throw new InvalidOperationException($"no brush token '{key}'");

    public static Color AsRendered(IBrush brush)
    {
        using var scene = Show(new Border { Background = brush }, width: 16, height: 16);

        return PaintedAt(scene.Window, new Point(8, 8));
    }

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
