using Avalonia.Media;

namespace Cockpit.Plugin.Diagram.Whiteboard.Rendering;

// Every colour the whiteboard draws with, deliberately outside the app theme (AC-860): a sheet of paper stays white
// under any repaint of the app around it, and an ink an agent has already been shown must keep meaning what it meant.
internal static class WhiteboardPalette
{
    public static readonly IBrush Paper = Brushes.White;
    public static readonly IBrush Outline = Brushes.Black;

    // One fixed visual language: freehand defaults to yellow, placed to this blue; WhiteboardObject.Color can override
    // either (null = default). PlacedColor is reserved — WhiteboardMcpTools' consent prompts promise this exact blue
    // marks the agent's work, so it must stay out of the operator's swatches and off place_on_whiteboard.
    public static readonly Color FreehandColor = Color.Parse("#F2C230");
    public static readonly Color PlacedColor = Color.Parse("#2563EB");
    public static readonly Color MarkerColor = Color.Parse("#FF7A1A");
    public static readonly Color StickyNoteColor = Color.Parse("#FDE68A");

    // AC-916: the operator's colour swatches — deliberately excludes PlacedColor (#2563EB), reserved for the
    // agent (see the header comment above). Each reads as both a 2.5px pencil stroke and a 0.35-alpha marker stroke.
    public static readonly IReadOnlyList<string> Swatches =
    [
        "#DC2626", // red
        "#EA580C", // orange
        "#16A34A", // green
        "#0D9488", // teal
        "#7C3AED", // purple
        "#DB2777", // pink
    ];

    public static readonly IBrush StickyNoteEdge = new SolidColorBrush(Color.Parse("#F5C518"));
    public static readonly IBrush StickyNoteText = new SolidColorBrush(Color.Parse("#3F3618"));
    public static readonly IBrush BadgeBackground = new SolidColorBrush(Color.Parse("#1F2937"), 0.85);
    public static readonly IBrush BadgeGlyph = Brushes.White;

    // The empty-canvas hint: a dot grid and a line of text on the paper.
    public static readonly IBrush GridDot = new SolidColorBrush(Color.Parse("#D6DEE8"));
    public static readonly IBrush HintText = new SolidColorBrush(Color.Parse("#94A3B8"));
}
