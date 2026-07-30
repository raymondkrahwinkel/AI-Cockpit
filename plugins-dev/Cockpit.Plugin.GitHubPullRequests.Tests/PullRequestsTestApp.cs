using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Cockpit.TestSupport;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

/// <summary>
/// The application the headless platform runs the side section/widget under. Both are built in code and carry no
/// styles of their own — the cockpit supplies the base theme, so a test host that leaves it out gets untemplated
/// controls that measure to nothing (and a "faint, not screaming" brush check that cannot fail honestly, since
/// every brush lookup would resolve to nothing).
/// </summary>
/// <remarks>
/// Loads the same styles, in the same order, as <c>src/Cockpit.App/App.axaml</c> (AC-338): Fluent, the DataGrid's
/// own Fluent theme, then the cockpit's own <c>Theme.axaml</c> read off disk (the plugin cannot reference
/// <c>Cockpit.App</c>). Neither control under test places a DataGrid, but Theme.axaml styles
/// <c>DataGridRow</c> directly — parsing the file at all requires the type to resolve, so the DataGrid theme
/// stays in the stack even though nothing here shows one (same as the GitHub-issues/YouTrack plugins' test apps).
/// </remarks>
public sealed class PullRequestsTestApp : Application
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
