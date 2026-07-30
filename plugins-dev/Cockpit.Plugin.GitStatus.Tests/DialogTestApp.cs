using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Cockpit.TestSupport;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.GitStatus.Tests;

/// <summary>
/// The application the headless platform runs the dialog under. The plugin's controls are built in code and carry no
/// styles of their own — the cockpit supplies the base theme, so a test host that leaves it out gets untemplated
/// controls that measure to nothing, and the state cell's <c>CockpitStatusErrorBrush</c>/etc. lookups resolve to
/// nothing.
/// </summary>
/// <remarks>
/// Loads the same styles, in the same order, as <c>src/Cockpit.App/App.axaml</c> (AC-338): Fluent, then the
/// material icon set, then the DataGrid's own Fluent theme, then the cockpit's <c>Theme.axaml</c> read off disk
/// (the plugin cannot reference <c>Cockpit.App</c>). Same arrangement the GitHubIssues/YouTrack plugin test
/// projects use.
/// </remarks>
public sealed class DialogTestApp : Application
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
