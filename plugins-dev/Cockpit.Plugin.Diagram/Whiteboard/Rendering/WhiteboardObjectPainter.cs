using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cockpit.Plugin.Diagram.Whiteboard.Model;

namespace Cockpit.Plugin.Diagram.Whiteboard.Rendering;

// One fixed visual language, shared by the live canvas and the raster snapshot: freehand is always yellow, a
// placed object (template or paste) is always this crisp blue. The distinction lives in WhiteboardObjectKind
// already; this is only how that distinction is drawn.
internal static class WhiteboardObjectPainter
{
    public static readonly Color FreehandColor = Color.Parse("#F2C230");
    public static readonly Color PlacedColor = Color.Parse("#2563EB");
    public static readonly Color MarkerColor = Color.Parse("#FF7A1A");
    public static readonly Color StickyNoteColor = Color.Parse("#FDE68A");

    private static readonly IBrush FreehandBrush = new SolidColorBrush(FreehandColor);
    private static readonly IBrush PlacedBrush = new SolidColorBrush(PlacedColor);
    private static readonly IPen PlacedPen = new Pen(PlacedBrush, 2);

    // Semi-transparent by construction — this is what "halfdoorzichtig" and "onderscheidbaar van potlood" mean in
    // practice: the same stroke geometry as the pencil, just a translucent brush and (at the call site) thicker.
    private static readonly IBrush MarkerBrush = new SolidColorBrush(MarkerColor, 0.35);

    private static readonly IBrush StickyNoteBrush = new SolidColorBrush(StickyNoteColor);
    private static readonly IPen StickyNotePen = new Pen(new SolidColorBrush(Color.Parse("#F5C518")), 1);
    private static readonly IBrush StickyNoteTextBrush = new SolidColorBrush(Color.Parse("#3F3618"));
    private static readonly IBrush BadgeBackground = new SolidColorBrush(Color.Parse("#1F2937"), 0.85);

    // The badge every renderer of a placed object pins on it: whose mark it is (AC-854) or where a picture came from.
    public static string? BadgeFor(PlacedObject placed) => placed switch
    {
        { PlacedByAgent: true } => "neergezet · agent",
        { IsPastedScreenshot: true } => "geplakt · screenshot",
        _ => null,
    };

    public static void PaintFreehand(DrawingContext context, IReadOnlyList<WhiteboardPoint> points, double thickness, bool isMarker = false)
    {
        if (points.Count < 2)
        {
            return;
        }

        var brush = isMarker ? MarkerBrush : FreehandBrush;
        var pen = new Pen(brush, thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
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

    public static void PaintPlaced(DrawingContext context, PlacedShapeKind kind, Rect rect, string? text, Bitmap? image, string? badge = null)
    {
        var textBrush = PlacedBrush;

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
            case PlacedShapeKind.StickyNote:
                context.DrawRectangle(StickyNoteBrush, StickyNotePen, rect);
                textBrush = StickyNoteTextBrush;
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
            _PaintText(context, rect, text, textBrush);
        }

        if (!string.IsNullOrEmpty(badge))
        {
            _PaintBadge(context, rect, badge);
        }
    }

    private static void _PaintText(DrawingContext context, Rect rect, string text, IBrush brush)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            14,
            brush)
        {
            MaxTextWidth = Math.Max(1, rect.Width - 8),
            MaxTextHeight = Math.Max(1, rect.Height - 8),
        };

        context.DrawText(formatted, rect.TopLeft + new Point(4, 4));
    }

    // A small pill pinned to the bottom-left corner — used today for "geplakt · screenshot" on a clipboard paste.
    private static void _PaintBadge(DrawingContext context, Rect rect, string badge)
    {
        var formatted = new FormattedText(
            badge,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            10,
            Brushes.White);

        var pillRect = new Rect(rect.Left + 4, rect.Bottom - formatted.Height - 8, formatted.Width + 12, formatted.Height + 6);
        context.DrawRectangle(BadgeBackground, null, pillRect, 4, 4);
        context.DrawText(formatted, pillRect.TopLeft + new Point(6, 3));
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
