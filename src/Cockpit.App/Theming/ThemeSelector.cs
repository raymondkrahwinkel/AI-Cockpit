using Avalonia.Platform;
using Avalonia.Styling;
using Cockpit.Core.Layout;

namespace Cockpit.App.Theming;

// AC-860: turns the chosen stand plus the system's preference into the one variant the app runs, and hands that
// variant out only when it actually changes. The stand is the setting; the variant is never written back to it.
internal sealed class ThemeSelector
{
    private readonly Action<ThemeVariant> _apply;

    public ThemeSelector(ThemeMode mode, PlatformThemeVariant system, Action<ThemeVariant> apply)
    {
        Mode = mode;
        System = system;
        _apply = apply;
    }

    public ThemeMode Mode { get; private set; }

    public PlatformThemeVariant System { get; private set; }

    public ThemeVariant Active => Resolve(Mode, System);

    public static ThemeVariant Resolve(ThemeMode mode, PlatformThemeVariant system) => mode switch
    {
        ThemeMode.Light => ThemeVariant.Light,
        ThemeMode.Dark => ThemeVariant.Dark,
        _ => system == PlatformThemeVariant.Light ? ThemeVariant.Light : ThemeVariant.Dark,
    };

    public void SetMode(ThemeMode mode)
    {
        Mode = mode;
        _apply(Active);
    }

    // A fixed choice does not move when the OS does; only `System` lets the change through.
    public void SetSystem(PlatformThemeVariant system)
    {
        var before = Active;
        System = system;
        if (Active != before)
        {
            _apply(Active);
        }
    }
}
