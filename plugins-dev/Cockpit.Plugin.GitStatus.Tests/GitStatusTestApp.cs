using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Cockpit.TestSupport;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.GitStatus.Tests;

// The application the headless platform runs the plugin's controls under (renamed from `DialogTestApp`
// when AC-522 removed the dialog it was originally named for — `GitStatusHeaderControlTests` is
// its only user now). The plugin's controls are built in code and carry no styles of their own — the cockpit
// supplies the base theme, so a test host that leaves it out gets untemplated controls that measure to
// nothing, and the badge's `CockpitStatusDoneBrush`/etc. lookups resolve to nothing.
// Loads the same styles, in the same order, as `src/Cockpit.App/App.axaml` (AC-338): Fluent, then the
// material icon set, then the DataGrid's own Fluent theme, then the cockpit's `Theme.axaml` read off disk
// (the plugin cannot reference `Cockpit.App`). The DataGrid theme stays even though this plugin no longer
// has one of its own (AC-522 removed its only `DataGrid`, the dialog): `Theme.axaml` itself styles
// `DataGridRow` for the plugins that still do, and XamlX cannot resolve that selector unless the
// assembly is already loaded — measured, not assumed: dropping this line broke every test here with
// "Unable to resolve type DataGridRow" the moment `Theme.axaml` parsed.
public sealed class GitStatusTestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new MaterialIconStyles(null));
        Styles.Add(new StyleInclude(new Uri("avares://Cockpit.Plugin.GitStatus.Tests/"))
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
