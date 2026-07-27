using Avalonia;
using Avalonia.Media;

namespace Cockpit.App.Theming;

/// <summary>
/// Resolves a Cockpit theme brush by its <c>Theme.axaml</c> resource key at call time, so a control that paints
/// outside Avalonia's styling system (a custom <see cref="Control.Render"/> override, or a visual tree built in
/// code like <see cref="Cockpit.App.Views.MarkdownView"/>) still follows a theme swap instead of freezing whatever
/// colour was current when its type first loaded. Previously duplicated as <c>MicLevelMeter._Resource</c>.
/// <c>Cockpit.Plugins.Abstractions</c> keeps its own copy of this same shape rather than referencing this one
/// (it already could — <c>Cockpit.App.csproj</c> references Abstractions, not the other way round): moving it
/// there would make it public SDK API, a permanent contract every plugin author could then depend on and that a
/// version bump would be needed to change. Retiring the SDK's own hand-written <c>_Brush</c> copies in favour of a
/// shared helper is a separate ticket.
/// </summary>
public static class ThemeBrush
{
    /// <summary>
    /// The named brush from the app's live resources, or a brush parsed from <paramref name="fallbackHex"/> when no
    /// <see cref="Application"/> resources exist (design-time preview, unit tests).
    /// </summary>
    public static IBrush Resolve(string key, string fallbackHex) =>
        Application.Current is { } app && app.TryGetResource(key, null, out var value) && value is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallbackHex));
}
