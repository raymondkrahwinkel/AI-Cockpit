namespace Cockpit.Infrastructure.Diagrams;

// The seven color roles Mermaider's RenderOptions exposes, plus the base font size in px. The caller feeds
// this from the host's own theme (Theme.axaml in Cockpit.App) so a diagram follows the app instead of
// bringing Mermaider's own default palette.
public sealed record MermaidTheme(
    string Bg,
    string Fg,
    string Line,
    string Accent,
    string Muted,
    string Surface,
    string Border,
    double FontSizePx)
{
    // For a render nobody looks at — checking a source is renderable, or reading its structure back (AC-808/852/841).
    // Any valid theme renders the same structure, and Infrastructure cannot see the app's own theme anyway.
    public static MermaidTheme Neutral { get; } = new(
        Bg: "#000000", Fg: "#ffffff", Line: "#888888", Accent: "#5b8def",
        Muted: "#999999", Surface: "#111111", Border: "#888888", FontSizePx: 13);
}
