using Avalonia.Media;

namespace Cockpit.TestSupport;

/// <summary>
/// WCAG 2.x contrast-ratio maths (relative luminance, then the (L1+0.05)/(L2+0.05) formula), so a re-tint is held
/// to the same AA floor a screen reader user's browser would check rather than to an eyeball guess (AC-381 — the
/// accent moved off #3b82f6 because that measured 3.68:1 against white, short of the 4.5:1 normal-text floor a
/// 12.5px SemiBold button label needs).
/// </summary>
public static class WcagContrast
{
    /// <summary>
    /// WCAG AA for normal text — and for a bold label under 14px, which does not qualify for the softer large-text
    /// floor below no matter how bold it is drawn.
    /// </summary>
    public const double AaNormalText = 4.5;

    /// <summary>WCAG AA once text is large enough to earn the softer floor: >=18.66px regular, or >=14px bold.</summary>
    public const double AaLargeText = 3.0;

    /// <summary>The ratio between two colours' relative luminance, per WCAG 2.x: (L_lighter + 0.05) / (L_darker + 0.05).</summary>
    public static double Ratio(Color a, Color b)
    {
        var la = _RelativeLuminance(a);
        var lb = _RelativeLuminance(b);
        var (lighter, darker) = la >= lb ? (la, lb) : (lb, la);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double _RelativeLuminance(Color colour) =>
        0.2126 * _Linearize(colour.R) + 0.7152 * _Linearize(colour.G) + 0.0722 * _Linearize(colour.B);

    /// <summary>sRGB gamma expansion of one 0-255 channel into the linear-light value the luminance formula wants.</summary>
    private static double _Linearize(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
