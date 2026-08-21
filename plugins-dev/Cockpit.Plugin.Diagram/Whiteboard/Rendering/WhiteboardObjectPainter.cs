using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cockpit.Plugin.Diagram.Whiteboard.Model;

namespace Cockpit.Plugin.Diagram.Whiteboard.Rendering;

// One fixed visual language: freehand defaults to yellow, placed defaults to this blue; WhiteboardObject.Color can
// override either (null = default). PlacedColor is reserved — WhiteboardMcpTools' consent prompts promise this
// exact blue marks the agent's work, so it must stay out of the operator's palette and off place_on_whiteboard.

// The compact icon a placed object shows for its badge: a one-letter glyph on the object, the full text in the
// tooltip the control attaches (see PlacedObjectControl) — no text pill, so it never outgrows a small object.
internal readonly record struct PlacedBadge(string Glyph, string Tooltip);

internal static class WhiteboardObjectPainter
{
    public static readonly Color FreehandColor = Color.Parse("#F2C230");
    public static readonly Color PlacedColor = Color.Parse("#2563EB");
    public static readonly Color MarkerColor = Color.Parse("#FF7A1A");
    public static readonly Color StickyNoteColor = Color.Parse("#FDE68A");

    // AC-916: the operator's colour swatches — deliberately excludes PlacedColor (#2563EB), reserved for the
    // agent (see the header comment above). Each reads as both a 2.5px pencil stroke and a 0.35-alpha marker stroke.
    public static readonly IReadOnlyList<string> Palette =
    [
        "#DC2626", // red
        "#EA580C", // orange
        "#16A34A", // green
        "#0D9488", // teal
        "#7C3AED", // purple
        "#DB2777", // pink
    ];

    private static readonly IBrush FreehandBrush = new SolidColorBrush(FreehandColor);
    private static readonly IBrush PlacedBrush = new SolidColorBrush(PlacedColor);
    private static readonly IPen PlacedPen = new Pen(PlacedBrush, 2);

    // Semi-transparent by construction — this is what "semi-transparent" and "distinguishable from the pencil" mean
    // in practice: the same stroke geometry as the pencil, just a translucent brush and (at the call site) thicker.
    private static readonly IBrush MarkerBrush = new SolidColorBrush(MarkerColor, 0.35);

    private static readonly IBrush StickyNoteBrush = new SolidColorBrush(StickyNoteColor);
    private static readonly IPen StickyNotePen = new Pen(new SolidColorBrush(Color.Parse("#F5C518")), 1);
    private static readonly IBrush StickyNoteTextBrush = new SolidColorBrush(Color.Parse("#3F3618"));
    private static readonly IBrush BadgeBackground = new SolidColorBrush(Color.Parse("#1F2937"), 0.85);

    // The badge every renderer of a placed object can pin on it: whose mark it is (AC-854) or where a picture came
    // from. Only drawn on hover/selection (AC-918) — the glyph is what's on the object, the tooltip is the full text.
    public static PlacedBadge? BadgeFor(PlacedObject placed) => placed switch
    {
        { PlacedByAgent: true } => new PlacedBadge("A", "Placed by agent"),
        { IsPastedScreenshot: true } => new PlacedBadge("S", "Pasted from screenshot"),
        _ => null,
    };

    public static void PaintFreehand(DrawingContext context, IReadOnlyList<WhiteboardPoint> points, double thickness, bool isMarker = false, string? color = null)
    {
        if (points.Count < 2)
        {
            return;
        }

        var brush = color is null
            ? (isMarker ? MarkerBrush : FreehandBrush)
            : new SolidColorBrush(_ResolveColor(color, FreehandColor), isMarker ? 0.35 : 1);
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

    // AC-916: `color` only reaches the line-drawn kinds below — StickyNote keeps its fixed fill/border/text
    // contrast and Image has no line to colour, so both ignore it regardless of what WhiteboardObject.Color holds.
    public static void PaintPlaced(DrawingContext context, PlacedShapeKind kind, Rect rect, string? text, Bitmap? image, string? color = null, PlacedBadge? badge = null, bool showBadge = false)
    {
        var brush = color is null ? PlacedBrush : new SolidColorBrush(_ResolveColor(color, PlacedColor));
        var pen = color is null ? PlacedPen : new Pen(brush, 2);
        var textBrush = brush;

        switch (kind)
        {
            case PlacedShapeKind.Rectangle:
                context.DrawRectangle(null, pen, rect);
                break;
            case PlacedShapeKind.RoundedRectangle:
                context.DrawRectangle(null, pen, rect, 8, 8);
                break;
            case PlacedShapeKind.Ellipse:
                context.DrawEllipse(null, pen, rect);
                break;
            case PlacedShapeKind.Diamond:
                context.DrawGeometry(null, pen, _Diamond(rect));
                break;
            case PlacedShapeKind.Arrow:
                _PaintArrow(context, rect, brush, pen);
                break;
            case PlacedShapeKind.Column:
                _PaintColumn(context, rect, pen);
                break;
            case PlacedShapeKind.Callout:
                _PaintCallout(context, rect, pen);
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

        if (showBadge && badge is { } b)
        {
            _PaintBadge(context, rect, b);
        }
    }

    private static Color _ResolveColor(string? hex, Color fallback) =>
        hex is { } h && Color.TryParse(h, out var parsed) ? parsed : fallback;

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

    private const double _BadgeDiameter = 16;
    private const double _BadgeMargin = 3;

    // A small fixed-size glyph disc, not a text pill — its size never depends on the badge text, so it fits a
    // 40x40 object. Pinned inside the bottom-right corner when the object has room, or drawn just above the
    // object (same idea as DiagramWorkspaceBody's _DrawAgentCursor tag) when it doesn't, so it never covers it.
    private static void _PaintBadge(DrawingContext context, Rect rect, PlacedBadge badge)
    {
        var fitsInside = rect.Width >= _BadgeDiameter + (_BadgeMargin * 2) && rect.Height >= _BadgeDiameter + (_BadgeMargin * 2);
        var origin = fitsInside
            ? new Point(rect.Right - _BadgeDiameter - _BadgeMargin, rect.Bottom - _BadgeDiameter - _BadgeMargin)
            : new Point(rect.Left, rect.Top - _BadgeDiameter - 2);

        var circleRect = new Rect(origin, new Size(_BadgeDiameter, _BadgeDiameter));
        context.DrawEllipse(BadgeBackground, null, circleRect);

        var formatted = new FormattedText(
            badge.Glyph,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            9,
            Brushes.White);
        context.DrawText(formatted, origin + new Point((_BadgeDiameter - formatted.Width) / 2, (_BadgeDiameter - formatted.Height) / 2));
    }

    private static void _PaintArrow(DrawingContext context, Rect rect, IBrush brush, IPen pen)
    {
        var y = rect.Center.Y;
        var headSize = Math.Min(14, (rect.Height / 2) + 4);

        context.DrawLine(pen, new Point(rect.Left, y), new Point(rect.Right - headSize, y));

        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(new Point(rect.Right - headSize, y - headSize), isFilled: true);
            ctx.LineTo(new Point(rect.Right, y));
            ctx.LineTo(new Point(rect.Right - headSize, y + headSize));
            ctx.EndFigure(true);
        }

        context.DrawGeometry(brush, null, head);
    }

    // The classic flowchart "predefined process" symbol: a rectangle with two inner verticals near its edges.
    private static void _PaintColumn(DrawingContext context, Rect rect, IPen pen)
    {
        context.DrawRectangle(null, pen, rect);
        var inset = Math.Min(10, rect.Width / 4);
        context.DrawLine(pen, new Point(rect.Left + inset, rect.Top), new Point(rect.Left + inset, rect.Bottom));
        context.DrawLine(pen, new Point(rect.Right - inset, rect.Top), new Point(rect.Right - inset, rect.Bottom));
    }

    // A rounded body plus a short open notch standing in for a speech-bubble tail — cheaper than one continuous
    // arced path and reads the same at whiteboard scale.
    private static void _PaintCallout(DrawingContext context, Rect rect, IPen pen)
    {
        var bodyHeight = rect.Height - Math.Min(14, rect.Height / 4);
        var body = new Rect(rect.X, rect.Y, rect.Width, bodyHeight);
        context.DrawRectangle(null, pen, body, 8, 8);

        var tail = new StreamGeometry();
        using (var ctx = tail.Open())
        {
            ctx.BeginFigure(new Point(rect.Left + (rect.Width * 0.15), body.Bottom), isFilled: false);
            ctx.LineTo(new Point(rect.Left + (rect.Width * 0.25), rect.Bottom));
            ctx.LineTo(new Point(rect.Left + (rect.Width * 0.35), body.Bottom));
            ctx.EndFigure(false);
        }

        context.DrawGeometry(null, pen, tail);
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
