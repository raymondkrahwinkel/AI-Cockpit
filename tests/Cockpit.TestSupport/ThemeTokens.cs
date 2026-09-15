using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cockpit.TestSupport;

// AC-860: a lookup naming no variant does not look inside ThemeDictionaries and comes back unset in both (Avalonia 12.1.1).
public static class ThemeTokens
{
    public static Color Colour(string key)
    {
        var app = Application.Current
            ?? throw new InvalidOperationException("No application is running, so no theme token can be resolved.");

        return app.TryFindResource(key, app.ActualThemeVariant, out var value) && value is Color colour
            ? colour
            : throw new InvalidOperationException($"The theme has no colour token '{key}' in its {app.ActualThemeVariant} variant.");
    }
}
