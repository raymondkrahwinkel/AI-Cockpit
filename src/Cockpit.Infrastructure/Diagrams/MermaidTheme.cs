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
    double FontSizePx);
