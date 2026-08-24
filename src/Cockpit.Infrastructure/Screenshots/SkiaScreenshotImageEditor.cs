using SkiaSharp;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

// Crops a capture with SkiaSharp (AC-329) — the same library the Windows blit and the macOS composition already
// encode through, so a screenshot passes through one imaging stack from the screen to the session.
internal sealed class SkiaScreenshotImageEditor : IScreenshotImageEditor, ISingletonService
{
    // How coarse a redaction block is, in the image's pixels. Big enough that a line of text inside one is a
    // single flat square rather than a smear that still has letter shapes in it — the failure mode of every
    // redaction that gets reversed.
    private const int BlockSize = 16;

    public byte[] Crop(byte[] png, CaptureRect region)
    {
        using var image = CaptureBitmap.Decode(png, "The capture");

        // Clamped rather than trusted. The region comes from a selection surface working in this image's pixels,
        // but a display that changed between the capture and the confirm would put it past the edge — and Skia
        // answers an out-of-bounds extract with an empty bitmap that encodes to a valid, blank PNG.
        var bounds = new SKRectI(
            Math.Clamp(region.X, 0, image.Width),
            Math.Clamp(region.Y, 0, image.Height),
            Math.Clamp(region.Right, 0, image.Width),
            Math.Clamp(region.Bottom, 0, image.Height));

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException($"The region {region} lies outside the {image.Width}×{image.Height} capture.");
        }

        using var cropped = new SKBitmap(bounds.Width, bounds.Height, image.ColorType, image.AlphaType);
        if (!image.ExtractSubset(cropped, bounds))
        {
            throw new InvalidOperationException($"The region {region} could not be taken out of the capture.");
        }

        using var encoded = SKImage.FromBitmap(cropped).Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("The cropped capture could not be encoded as a PNG.");

        return encoded.ToArray();
    }

    public byte[] Burn(byte[] png, IReadOnlyList<Mark> marks)
    {
        if (marks.Count == 0)
        {
            return png;
        }

        using var image = CaptureBitmap.Decode(png, "The capture");

        // In the order they were placed, because that is what the operator watched happen: a frame drawn over a
        // pixelated box and one drawn under it are different pictures, and the surface showed them the first.
        foreach (var mark in marks)
        {
            switch (mark)
            {
                case RedactionMark redaction:
                    _Pixelate(image, redaction.Area);
                    break;
                case OutlineMark outline:
                    _Outline(image, outline);
                    break;
                case ArrowMark arrow:
                    _Arrow(image, arrow);
                    break;
                case HighlightMark highlight:
                    _Highlight(image, highlight);
                    break;
                case StrokeMark stroke:
                    _Stroke(image, stroke);
                    break;
                case TextMark note:
                    _Text(image, note);
                    break;
                default:
                    throw new NotSupportedException($"There is no way to burn in a {mark.GetType().Name}.");
            }
        }

        using var encoded = SKImage.FromBitmap(image).Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("The marked capture could not be encoded as a PNG.");

        return encoded.ToArray();
    }

    // Drawn straight onto the bitmap so the canvas clips whatever falls outside the crop. Inset by half the
    // stroke because Skia centres a stroke on the path — on the exact rectangle a frame drawn to the image's
    // edge would have half its width clipped away and read thinner on that side than the others.
    private static void _Outline(SKBitmap image, OutlineMark outline)
    {
        using var canvas = new SKCanvas(image);
        using var paint = new SKPaint
        {
            Color = new SKColor(outline.Colour),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = outline.Thickness,
            IsAntialias = true,
        };

        var inset = outline.Thickness / 2f;
        var area = outline.Area;
        canvas.DrawRect(
            new SKRect(area.X + inset, area.Y + inset, area.Right - inset, area.Bottom - inset),
            paint);
    }

    // The plate is sized from what the font actually measures, not guessed from character count, so letters
    // never hang off a too-narrow plate. The surface and imaging library use different font stacks, so the
    // plate can differ by a few pixels between preview and picture — text and position stay identical.
    private static void _Text(SKBitmap image, TextMark note)
    {
        using var canvas = new SKCanvas(image);
        using var font = new SKFont(SKTypeface.Default, note.Size);
        using var letters = new SKPaint { Color = new SKColor(note.Colour), IsAntialias = true };
        using var plate = new SKPaint { Color = new SKColor(note.Plate), Style = SKPaintStyle.Fill, IsAntialias = true };

        var padding = (float)note.Padding;
        var width = font.MeasureText(note.Text);
        var metrics = font.Metrics;
        var height = metrics.Descent - metrics.Ascent;

        canvas.DrawRoundRect(
            new SKRect(note.At.X, note.At.Y, note.At.X + width + (padding * 2), note.At.Y + height + (padding * 2)),
            padding / 2,
            padding / 2,
            plate);

        // Drawn from the baseline, which is where the font puts letters, and the baseline is one ascent below the
        // top of the plate's inside — the corner is where the operator clicked, not where the letters sit.
        canvas.DrawText(note.Text, note.At.X + padding, note.At.Y + padding - metrics.Ascent, font, letters);
    }

    // Draws the freehand line in its own colour only, rounded at joins/ends since a hand makes no corners.
    // AC-1013: dropped the contrasting outline ring added pre-AC-375 to survive an unknown background —
    // once the operator picks the ink colour themselves, an unrequested white ring around it is unwanted.
    private static void _Stroke(SKBitmap image, StrokeMark stroke)
    {
        if (stroke.Start() is not { } start || stroke.Curve() is not { Count: > 0 } curves)
        {
            return;
        }

        using var canvas = new SKCanvas(image);
        using var path = new SKPath();

        path.MoveTo((float)start.X, (float)start.Y);
        foreach (var curve in curves)
        {
            path.CubicTo(
                (float)curve.FirstControl.X, (float)curve.FirstControl.Y,
                (float)curve.SecondControl.X, (float)curve.SecondControl.Y,
                (float)curve.End.X, (float)curve.End.Y);
        }

        using var line = _Pen(stroke.Colour, stroke.Thickness);

        canvas.DrawPath(path, line);
    }

    private static SKPaint _Pen(uint colour, float width) => new()
    {
        Color = new SKColor(colour),
        Style = SKPaintStyle.Stroke,
        StrokeWidth = width,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
        IsAntialias = true,
    };

    // AC-1013: washes the band in its colour via multiply/screen blend (not opaque composite) so the underlying
    // text stays readable — multiplying keeps most of the contrast ratio instead of collapsing it, and stacked
    // bands deepen like repeated marker passes. Dropped: the ~20:1-to-3:1 contrast math behind that choice.
    private static void _Highlight(SKBitmap image, HighlightMark highlight)
    {
        using var canvas = new SKCanvas(image);
        using var paint = new SKPaint
        {
            Color = new SKColor(highlight.Wash),
            Style = SKPaintStyle.Fill,
            BlendMode = highlight.Blend == HighlightBlend.Darken ? SKBlendMode.Multiply : SKBlendMode.Screen,
        };

        var area = highlight.Area;
        canvas.DrawRect(new SKRect(area.X, area.Y, area.Right, area.Bottom), paint);
    }

    // Draws the arrow as one closed shape (shaft+head together), filled; the shape geometry itself comes from
    // the mark, not here, so only one thing decides what the arrow looks like. AC-1013: dropped the contrasting
    // outline ring carried pre-AC-375 for an unknown background — the operator's ink colour now covers that.
    private static void _Arrow(SKBitmap image, ArrowMark arrow)
    {
        if (arrow.Silhouette() is not { Count: > 0 } corners)
        {
            return;
        }

        using var canvas = new SKCanvas(image);
        using var path = new SKPath();

        path.MoveTo((float)corners[0].X, (float)corners[0].Y);
        foreach (var corner in corners.Skip(1))
        {
            path.LineTo((float)corner.X, (float)corner.Y);
        }

        path.Close();

        using var body = new SKPaint
        {
            Color = new SKColor(arrow.Colour),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        canvas.DrawPath(path, body);
    }

    // Replaces each block of the region with its own average colour. Averaging rather than sampling one pixel
    // of the block: a block that took its colour from a corner keeps whatever happened to be there, which for a
    // character's stroke is the character.
    private static void _Pixelate(SKBitmap image, CaptureRect region)
    {
        var left = Math.Clamp(region.X, 0, image.Width);
        var top = Math.Clamp(region.Y, 0, image.Height);
        var right = Math.Clamp(region.Right, 0, image.Width);
        var bottom = Math.Clamp(region.Bottom, 0, image.Height);

        for (var blockTop = top; blockTop < bottom; blockTop += BlockSize)
        {
            for (var blockLeft = left; blockLeft < right; blockLeft += BlockSize)
            {
                var blockRight = Math.Min(blockLeft + BlockSize, right);
                var blockBottom = Math.Min(blockTop + BlockSize, bottom);
                var colour = _AverageOf(image, blockLeft, blockTop, blockRight, blockBottom);

                for (var y = blockTop; y < blockBottom; y++)
                {
                    for (var x = blockLeft; x < blockRight; x++)
                    {
                        image.SetPixel(x, y, colour);
                    }
                }
            }
        }
    }

    private static SKColor _AverageOf(SKBitmap image, int left, int top, int right, int bottom)
    {
        long red = 0, green = 0, blue = 0;
        var count = 0;

        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var pixel = image.GetPixel(x, y);
                red += pixel.Red;
                green += pixel.Green;
                blue += pixel.Blue;
                count++;
            }
        }

        return count == 0
            ? SKColors.Black
            : new SKColor((byte)(red / count), (byte)(green / count), (byte)(blue / count));
    }
}
