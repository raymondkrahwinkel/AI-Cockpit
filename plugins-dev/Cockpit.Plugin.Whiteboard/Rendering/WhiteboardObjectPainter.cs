using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cockpit.Plugin.Whiteboard.Model;

namespace Cockpit.Plugin.Whiteboard.Rendering;

// One fixed visual language, shared by the live canvas and the raster snapshot: freehand is always yellow, a
// placed object (template or paste) is always this crisp blue. The distinction lives in WhiteboardObjectKind
// already; this is only how that distinction is drawn.
internal static class WhiteboardObjectPainter
{
    public static readonly Color FreehandColor = Color.Parse("#F2C230");
    public static readonly Color PlacedColor = Color.Parse("#2563EB");

    private static readonly IBrush FreehandBrush = new SolidColorBrush(FreehandColor);
    private static readonly IBrush PlacedBrush = new SolidColorBrush(PlacedColor);
    private static readonly IPen PlacedPen = new Pen(PlacedBrush, 2);

    public static void PaintFreehand(DrawingContext context, IReadOnlyList<WhiteboardPoint> points, double thickness)
    {
        if (points.Count < 2)
        {
            return;
        }

        var pen = new Pen(FreehandBrush, thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: false);
            for (var i = 1; i < points.Count; i++)
            {
                ctx.LineTo(new Point(points[i].X, points[i].Y));
            }

            ctx.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    public static void PaintPlaced(DrawingContext context, PlacedShapeKind kind, Rect rect, string? text, Bitmap? image)
    {
        switch (kind)
        {
            case PlacedShapeKind.Rectangle:
                context.DrawRectangle(null, PlacedPen, rect);
                break;
            case PlacedShapeKind.RoundedRectangle:
                context.DrawRectangle(null, PlacedPen, rect, 8, 8);
                break;
            case PlacedShapeKind.Ellipse:
                context.DrawEllipse(null, PlacedPen, rect);
                break;
            case PlacedShapeKind.Diamond:
                context.DrawGeometry(null, PlacedPen, _Diamond(rect));
                break;
            case PlacedShapeKind.Arrow:
                _PaintArrow(context, rect);
                break;
            case PlacedShapeKind.Column:
                _PaintColumn(context, rect);
                break;
            case PlacedShapeKind.Callout:
                _PaintCallout(context, rect);
                break;
            case PlacedShapeKind.Text:
                break;
            case PlacedShapeKind.Image:
                if (image is not null)
                {
                    context.DrawImage(image, new Rect(image.Size), rect);
                }

                break;
        }

        if (kind != PlacedShapeKind.Image && !string.IsNullOrEmpty(text))
        {
            _PaintText(context, rect, text);
        }
    }

    private static void _PaintText(DrawingContext context, Rect rect, string text)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            14,
            PlacedBrush)
        {
            MaxTextWidth = Math.Max(1, rect.Width - 8),
            MaxTextHeight = Math.Max(1, rect.Height - 8),
        };

        context.DrawText(formatted, rect.TopLeft + new Point(4, 4));
    }

    private static void _PaintArrow(DrawingContext context, Rect rect)
    {
        var y = rect.Center.Y;
        var headSize = Math.Min(14, (rect.Height / 2) + 4);

        context.DrawLine(PlacedPen, new Point(rect.Left, y), new Point(rect.Right - headSize, y));

        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(new Point(rect.Right - headSize, y - headSize), isFilled: true);
            ctx.LineTo(new Point(rect.Right, y));
            ctx.LineTo(new Point(rect.Right - headSize, y + headSize));
            ctx.EndFigure(true);
        }

        context.DrawGeometry(PlacedBrush, null, head);
    }

    // The classic flowchart "predefined process" symbol: a rectangle with two inner verticals near its edges.
    private static void _PaintColumn(DrawingContext context, Rect rect)
    {
        context.DrawRectangle(null, PlacedPen, rect);
        var inset = Math.Min(10, rect.Width / 4);
        context.DrawLine(PlacedPen, new Point(rect.Left + inset, rect.Top), new Point(rect.Left + inset, rect.Bottom));
        context.DrawLine(PlacedPen, new Point(rect.Right - inset, rect.Top), new Point(rect.Right - inset, rect.Bottom));
    }

    // A rounded body plus a short open notch standing in for a speech-bubble tail — cheaper than one continuous
    // arced path and reads the same at whiteboard scale.
    private static void _PaintCallout(DrawingContext context, Rect rect)
    {
        var bodyHeight = rect.Height - Math.Min(14, rect.Height / 4);
        var body = new Rect(rect.X, rect.Y, rect.Width, bodyHeight);
        context.DrawRectangle(null, PlacedPen, body, 8, 8);

        var tail = new StreamGeometry();
        using (var ctx = tail.Open())
        {
            ctx.BeginFigure(new Point(rect.Left + (rect.Width * 0.15), body.Bottom), isFilled: false);
            ctx.LineTo(new Point(rect.Left + (rect.Width * 0.25), rect.Bottom));
            ctx.LineTo(new Point(rect.Left + (rect.Width * 0.35), body.Bottom));
            ctx.EndFigure(false);
        }

        context.DrawGeometry(null, PlacedPen, tail);
    }

    private static StreamGeometry _Diamond(Rect rect)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(new Point(rect.Center.X, rect.Top), isFilled: false);
        ctx.LineTo(new Point(rect.Right, rect.Center.Y));
        ctx.LineTo(new Point(rect.Center.X, rect.Bottom));
        ctx.LineTo(new Point(rect.Left, rect.Center.Y));
        ctx.EndFigure(true);
        return geometry;
    }
}
