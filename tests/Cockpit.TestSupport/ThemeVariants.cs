using Avalonia;
using Avalonia.Styling;

namespace Cockpit.TestSupport;

/// <summary>
/// The two variants <c>Theme.axaml</c> carries (AC-860), for a suite that has to hold in both. Named by string
/// because xunit can only enumerate theory data it can serialise, and <see cref="ThemeVariant"/> is not that.
/// </summary>
public static class ThemeVariants
{
    public static readonly string[] Names = ["Dark", "Light"];

    public static ThemeVariant Parse(string name) => name switch
    {
        "Dark" => ThemeVariant.Dark,
        "Light" => ThemeVariant.Light,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a theme variant this app ships"),
    };

    /// <summary>
    /// Runs <paramref name="body"/> with the application switched to <paramref name="name"/>, and switches it back
    /// afterwards — the test apps start dark, and a variant left behind would leak into the next test's render.
    /// </summary>
    public static T Under<T>(string name, Func<T> body)
    {
        var app = Application.Current
            ?? throw new InvalidOperationException("No application is running, so there is no theme variant to switch.");
        var before = app.RequestedThemeVariant;
        app.RequestedThemeVariant = Parse(name);
        try
        {
            return body();
        }
        finally
        {
            app.RequestedThemeVariant = before;
        }
    }

    public static void Under(string name, Action body) => Under(name, () =>
    {
        body();
        return 0;
    });
}
