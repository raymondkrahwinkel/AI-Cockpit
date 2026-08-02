using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Cockpit.TestSupport;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.SessionReview.Tests;

// The application the headless platform runs the review panel under. The panel is built in code and carries no
// styles of its own — the cockpit supplies the base theme, so a test host that leaves it out gets untemplated
// controls that measure to nothing and resolve every `Cockpit…Brush` lookup to nothing.
// Loads the same styles, in the same order, as `src/Cockpit.App/App.axaml` (AC-338): Fluent, then the
// material icon set, then the DataGrid's own Fluent theme, then the cockpit's `Theme.axaml` read off disk
// (the plugin cannot reference `Cockpit.App`). This plugin has no DataGrid, but the theme file itself styles
// `DataGridRow` for the plugins that do, and XamlX cannot resolve that selector unless the assembly is
// already loaded — the same reason the other plugin test apps carry the reference.
public sealed class SessionReviewTestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new MaterialIconStyles(null));
        Styles.Add(new StyleInclude(new Uri("avares://Cockpit.Plugin.SessionReview.Tests/"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml"),
        });
        Styles.Add(_CockpitTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    private static IStyle _CockpitTheme()
    {
        var path = Path.Combine(RepositoryPaths.Root, "src", "Cockpit.App", "Styles", "Theme.axaml");
        return AvaloniaRuntimeXamlLoader.Load(File.ReadAllText(path)) as IStyle
            ?? throw new InvalidOperationException($"{path} did not parse into a style.");
    }
}
