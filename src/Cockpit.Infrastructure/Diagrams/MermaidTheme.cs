namespace Cockpit.Infrastructure.Diagrams;

/// <summary>
/// The seven color roles Mermaider's <c>RenderOptions</c> exposes, plus the base font size in px. The caller
/// feeds this from the host's own theme (Theme.axaml in Cockpit.App) so a diagram follows the app instead of
/// bringing Mermaider's own default palette.
/// </summary>
public sealed record MermaidTheme(
    string Bg,
    string Fg,
    string Line,
    string Accent,
    string Muted,
    string Surface,
    string Border,
    double FontSizePx);
