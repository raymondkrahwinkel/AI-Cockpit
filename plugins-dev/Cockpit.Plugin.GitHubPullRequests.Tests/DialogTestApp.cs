using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Cockpit.TestSupport;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// The application the headless platform runs the settings view under. The plugin's controls are built in code
// and carry no styles of their own — the cockpit supplies the base theme, so a test host that leaves it out gets
// untemplated controls that measure to nothing.
// Loads the same styles, in the same order, as `src/Cockpit.App/App.axaml` (AC-338), the same way
// `Cockpit.Plugin.GitHubIssues.Tests` does: Fluent, then the material icon set, then the DataGrid's own
// Fluent theme, then the cockpit's `Theme.axaml` read off disk (the plugin cannot reference
// `Cockpit.App`). Without the cockpit theme, every `_Brush("Cockpit…")` lookup in the view resolves to
// nothing.
public sealed class DialogTestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new MaterialIconStyles(null));
        Styles.Add(new StyleInclude(new Uri("avares://Cockpit.Plugin.GitHubPullRequests.Tests/"))
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
