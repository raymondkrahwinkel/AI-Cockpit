using Avalonia;
using Avalonia.Styling;

namespace Cockpit.TestSupport;

// AC-860: named by string because xunit can only enumerate theory data it can serialise, and ThemeVariant is not that.
public static class ThemeVariants
{
    public static readonly string[] Names = ["Dark", "Light"];

    public static ThemeVariant Parse(string name) => name switch
    {
        "Dark" => ThemeVariant.Dark,
        "Light" => ThemeVariant.Light,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a theme variant this app ships"),
    };

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
