using Avalonia;
using Avalonia.Media;

namespace Cockpit.App.Theming;

// Resolves brushes at call time so code-rendered controls follow theme swaps. This remains App-local rather than
// becoming permanent public SDK API; Abstractions deliberately keeps its own equivalent helper.
public static class ThemeBrush
{
    // The named brush from the app's live resources, or a brush parsed from `fallbackHex` when no
    // `Application` resources exist (design-time preview, unit tests).
    public static IBrush Resolve(string key, string fallbackHex) =>
        Application.Current is { } app && app.TryGetResource(key, null, out var value) && value is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallbackHex));
}
