using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Cockpit.App;

/// <summary>
/// A desktop to stand in for the operator's, drawn into a bitmap for the selection surface's harness scenes to be
/// rendered over (AC-357).
/// </summary>
/// <remarks>
/// Drawn rather than filled: the surface dims what is outside the selection, strokes a line around what is inside
/// and paints a block over what is hidden, and all three look right over a flat colour no matter how wrong they
/// are. What is needed is somewhere genuinely light and somewhere genuinely dark, with detail at text scale in
/// each — which is also what tells you whether a redaction box covers anything.
/// <para>
/// Its own file because none of these colours are the cockpit's, deliberately: this is somebody else's screen, and
/// pointing it at theme tokens would make the stand-in follow a repaint of the very app it exists to be
/// independent of. That is what the theme-token guard's whole-file exemption names. Keeping it separate keeps the
/// exemption to a file that only ever draws a picture — the scene wiring next door stays guarded, and that is the
/// file every later ticket in AC-356 is going to touch.
/// </para>
/// </remarks>
internal static class StandInDesktop
{
    public static RenderTargetBitmap Draw(int width, int height)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
        using var context = bitmap.CreateDrawingContext();

        context.FillRectangle(
            new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#0f1420"), 0),
                    new GradientStop(Color.Parse("#243349"), 1),
                },
            },
            new Rect(0, 0, width, height));

        // An editor, dark, filling the left: the case a marquee stroke has to stay visible against.
        _Panel(context, _Area(width, height, 0.04, 0.08, 0.46, 0.74), "#161a22", "#232936");
        _Lines(context, _Area(width, height, 0.07, 0.18, 0.43, 0.70), "#7f8ea3", height * 0.014, [0.62, 0.88, 0.41, 0.75, 0.55, 0.93, 0.34, 0.68, 0.80, 0.47]);

        // A document, light, top right: the case the dim outside the selection has to be visible over.
        _Panel(context, _Area(width, height, 0.52, 0.08, 0.96, 0.50), "#fbfaf7", "#e6e3dd");
        _Lines(context, _Area(width, height, 0.55, 0.17, 0.93, 0.47), "#8d8880", height * 0.013, [0.90, 0.72, 0.85, 0.55, 0.78, 0.66]);

        // A terminal, near black, bottom right — the thing a redaction box most often has to cover.
        _Panel(context, _Area(width, height, 0.52, 0.54, 0.96, 0.86), "#080a0f", "#1a1f2b");
        _Lines(context, _Area(width, height, 0.55, 0.61, 0.93, 0.83), "#6ee7a8", height * 0.013, [0.48, 0.71, 0.36, 0.62, 0.29]);

        // A dock, and one bright tile on it, so the picture has a genuinely light spot outside the document too.
        context.FillRectangle(new SolidColorBrush(Color.Parse("#1c2331"), 0.85), _Area(width, height, 0.30, 0.90, 0.70, 0.97), (float)(height * 0.008));
        context.FillRectangle(new SolidColorBrush(Color.Parse("#f4c150")), _Area(width, height, 0.46, 0.915, 0.54, 0.955), (float)(height * 0.006));

        return bitmap;
    }

    private static void _Panel(DrawingContext context, Rect area, string body, string chrome)
    {
        context.FillRectangle(new SolidColorBrush(Color.Parse(body)), area);
        context.FillRectangle(new SolidColorBrush(Color.Parse(chrome)), area.WithHeight(area.Height * 0.09));
    }

    private static void _Lines(DrawingContext context, Rect area, string ink, double thickness, double[] widths)
    {
        var brush = new SolidColorBrush(Color.Parse(ink));
        var step = area.Height / widths.Length;

        for (var index = 0; index < widths.Length; index++)
        {
            context.FillRectangle(
                brush,
                new Rect(area.X, area.Y + (index * step), area.Width * widths[index], thickness),
                (float)(thickness / 2));
        }
    }

    private static Rect _Area(int width, int height, double left, double top, double right, double bottom) =>
        new(width * left, height * top, width * (right - left), height * (bottom - top));
}
