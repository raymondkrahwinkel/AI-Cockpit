using Avalonia.Media;

namespace Cockpit.Plugin.Diagram.Wireframe.Rendering;

// Greys and one font, so a wireframe reads as a sketch and never as a finished design (AC-871). The one exception is
// Accent (AC-903): `primary` marks the single thing the screen wants you to do, and grey cannot say that.
internal static class WireframePalette
{
    public static readonly IBrush Paper = new SolidColorBrush(Color.Parse("#FFFFFF"));
    public static readonly IBrush Tint = new SolidColorBrush(Color.Parse("#F2F2F2"));
    public static readonly IBrush Outline = new SolidColorBrush(Color.Parse("#B4B4B4"));
    public static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#2E2E2E"));
    public static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#767676"));
    public static readonly IBrush Skeleton = new SolidColorBrush(Color.Parse("#DCDCDC"));
    public static readonly IBrush Highlight = new SolidColorBrush(Color.Parse("#D0D0D0"));
    public static readonly IBrush Solid = new SolidColorBrush(Color.Parse("#4A4A4A"));
    public static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#3B6FB6"));
    public static readonly IBrush Scrim = new SolidColorBrush(Color.Parse("#33000000"));

    public const double TitleSize = 16;
    public const double TextSize = 13;
    public const double CaptionSize = 11;

    public const double Gap = 8;
    public const double Pad = 12;
    public const double ControlHeight = 30;
    public const double Radius = 3;
    public const double DisabledOpacity = 0.45;

    // The placeholder line: a wireframe shows where text lands and how much of it, never the words themselves, so a
    // component with no text of its own is drawn as bars rather than left blank.
    public const double LineHeight = 8;
    public const double LineGap = 6;
}
