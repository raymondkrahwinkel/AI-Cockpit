using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MermaidRenderOptions = Mermaider.Models.RenderOptions;

namespace Cockpit.Plugin.Diagram;

// AC-911: the seven Mermaid theme colors DiagramWorkspaceBody._RenderInto and the template previews both need, in
// one place. AC-860: read from the host's tokens at render time — the same seven AppMermaidTheme reads — so a
// diagram follows the app theme instead of the hand-copied dark palette it used to carry.
internal static class DiagramTheme
{
    public static MermaidRenderOptions Options => new()
    {
        Bg = _Hex(_Brush("CockpitPanelBgBrush", "#1a1d24")),
        Fg = _Hex(_Brush("CockpitTextPrimaryBrush", "#e8eaef")),
        Line = _Hex(_Brush("CockpitHairlineBrush", "#2a2f39")),
        Accent = _Hex(_Brush("CockpitAccentBrush", "#2563eb")),
        Muted = _Hex(_Brush("CockpitTextSecondaryBrush", "#949aa5")),
        Surface = _Hex(_Brush("CockpitInsetBgBrush", "#202430")),
        Border = _Hex(_Brush("CockpitHairlineBrush", "#2a2f39")),
        Font = "Inter",
        FontSize = "13px",
    };

    private static string _Hex(ISolidColorBrush brush) => $"#{brush.Color.R:x2}{brush.Color.G:x2}{brush.Color.B:x2}";

    // The host's theme brush, resolved at call time. The fallback hex is only reached with no `Application`
    // (designer, headless test) and is held equal to its token by the repository's theme guard.
    private static ISolidColorBrush _Brush(string key, string fallbackHex) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is ISolidColorBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallbackHex));
}
