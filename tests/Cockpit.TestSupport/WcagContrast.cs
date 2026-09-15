using Avalonia.Media;

namespace Cockpit.TestSupport;

// AC-381: the accent left #3b82f6 because it measured 3.68:1 on white, short of the 4.5:1 floor a 12.5px SemiBold label needs.
public static class WcagContrast
{
    // A bold label under 14px does not qualify for the softer large-text floor, however bold it is drawn.
    public const double AaNormalText = 4.5;

    public const double AaLargeText = 3.0;

    public static double Ratio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var (lighter, darker) = la >= lb ? (la, lb) : (lb, la);
        return (lighter + 0.05) / (darker + 0.05);
    }

    public static double RelativeLuminance(Color colour) =>
        0.2126 * _Linearize(colour.R) + 0.7152 * _Linearize(colour.G) + 0.0722 * _Linearize(colour.B);

    private static double _Linearize(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
