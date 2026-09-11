using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cockpit.TestSupport;

/// <summary>
/// A colour token as the running application resolves it, in the variant it is actually in. Since the colours
/// live once per variant in <c>ResourceDictionary.ThemeDictionaries</c> (AC-860), a lookup that names no variant
/// — <c>FindResource(key)</c>, <c>TryFindResource(key, out _)</c> — does not look inside them and comes back
/// unset in <em>both</em> variants (measured on Avalonia 12.1.1). Brushes and geometry sit outside and still
/// resolve either way; a colour has to come through here or through an explicit <c>ActualThemeVariant</c>.
/// </summary>
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
