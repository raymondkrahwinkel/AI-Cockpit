using Avalonia;

namespace Cockpit.App.Views;

internal enum CompanionOrientation
{
    Horizontal,
    Vertical,
}

internal readonly record struct CompanionPopOutPlacement(PixelRect Bounds, bool ContentScrolls);

// The one placement rule for every Companion pop-out: pure geometry, so a wrong spot shows in the renders.
internal static class CompanionPopOutPlacer
{
    internal const int Gap = 8;
    internal const int MinSide = 160;

    // Returns null when there is no room left; the strip itself is never moved.
    public static CompanionPopOutPlacement? Place(
        PixelRect workArea, PixelRect strip, CompanionOrientation orientation, PixelRect item, PixelSize desired, IReadOnlyList<PixelRect> open)
    {
        var horizontal = orientation == CompanionOrientation.Horizontal;
        var before = Math.Max(0, horizontal ? strip.Y - workArea.Y : strip.X - workArea.X);
        var after = Math.Max(0, horizontal ? workArea.Bottom - strip.Bottom : workArea.Right - strip.Right);

        foreach (var side in before > after ? new[] { -1, 1 } : new[] { 1, -1 })
        {
            var room = (side < 0 ? before : after) - Gap;
            var width = Math.Min(desired.Width, horizontal ? workArea.Width : room);
            var height = Math.Min(desired.Height, horizontal ? room : workArea.Height);
            if (width < Math.Min(MinSide, desired.Width) || height < Math.Min(MinSide, desired.Height))
            {
                continue;
            }

            // A vertical strip may also need a lower pop-out to fit beside one that is already open.
            var heights = horizontal ? new[] { height } : new[] { height, Math.Min(height, MinSide) };
            foreach (var h in heights.Distinct())
            {
                var size = new PixelSize(width, h);
                foreach (var cross in _CrossStarts(item, size, open, horizontal))
                {
                    var bounds = horizontal
                        ? new PixelRect(Math.Clamp(cross, workArea.X, workArea.Right - width), side < 0 ? strip.Y - Gap - h : strip.Bottom + Gap, width, h)
                        : new PixelRect(side < 0 ? strip.X - Gap - width : strip.Right + Gap, Math.Clamp(cross, workArea.Y, workArea.Bottom - h), width, h);
                    if (!bounds.Intersects(strip) && !open.Any(bounds.Intersects))
                    {
                        return new CompanionPopOutPlacement(bounds, bounds.Width < desired.Width || bounds.Height < desired.Height);
                    }
                }
            }
        }

        return null;
    }

    // Aligned on the item first, then beside each pop-out that is already open.
    private static IEnumerable<int> _CrossStarts(PixelRect item, PixelSize size, IReadOnlyList<PixelRect> open, bool horizontal)
    {
        yield return horizontal ? item.X : item.Y;
        foreach (var other in open)
        {
            yield return (horizontal ? other.Right : other.Bottom) + Gap;
            yield return (horizontal ? other.X - size.Width : other.Y - size.Height) - Gap;
        }
    }
}
