using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Infrastructure.Diagrams;

namespace Cockpit.App.Diagrams;

// Feeds MermaidRenderPipeline from the running app's own Theme.axaml tokens (AC-807), so a diagram follows
// the host theme instead of Mermaider's own default palette.
public static class AppMermaidTheme
{
    public static MermaidTheme FromCurrentTheme() => new(
        Bg: Hex("CockpitPanelBgColor"),
        Fg: Hex("CockpitTextPrimaryColor"),
        Line: Hex("CockpitHairlineColor"),
        Accent: Hex("CockpitAccentColor"),
        Muted: Hex("CockpitTextSecondaryColor"),
        Surface: Hex("CockpitInsetBgColor"),
        Border: Hex("CockpitHairlineColor"),
        FontSizePx: 13);

    private static string Hex(string resourceKey)
    {
        var app = Application.Current ?? throw new InvalidOperationException("no running Avalonia application");
        // In the actual variant: a colour token lives once per variant (AC-860) and a variant-less lookup misses it.
        var color = app.TryFindResource(resourceKey, app.ActualThemeVariant, out var value) && value is Color found
            ? found
            : throw new InvalidOperationException($"no token '{resourceKey}'");
        return $"#{color.R:x2}{color.G:x2}{color.B:x2}";
    }
}
