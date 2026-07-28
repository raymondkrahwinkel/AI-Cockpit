using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Cockpit.TestSupport;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// This plugin's half of the AC-338 theme-palette baseline: what its own dialog actually paints, held against a
/// committed file the same way <c>Cockpit.App.ViewTests.ThemePaletteBaselineTests</c> holds the host's. One
/// surface stands in for the plugin — the issue dialog, with a couple of issues loaded and one selected so the
/// list and the detail panel both render.
/// </summary>
/// <remarks>
/// To re-record after an intended change: run with <c>COCKPIT_UPDATE_THEME_BASELINES=1</c>, review the diff, then
/// run again without it. The rewriting run still fails on purpose.
/// </remarks>
[Collection("avalonia")]
public class ThemePaletteBaselineTests
{
    private static readonly YouTrackInstance LocalInstance = new("Local", "http://127.0.0.1:9/", string.Empty, string.Empty);
    private static readonly YouTrackIssue First = new("1-1", "AT-1", "Faster startup", "Cold start takes 4s.", "AT", "Backlog");
    private static readonly YouTrackIssue Second = new("1-2", "AT-2", "Fix the sidebar", "It collapses.", "AT", "Backlog");

    [Fact]
    public void TheDialog_PaintsNothingItsBaselineDoesNotAccountFor() => HeadlessAvalonia.Run(() =>
    {
        var painted = _Painted();

        var baseline = Path.Combine(RepositoryPaths.Root, "plugins-dev", "Cockpit.Plugin.YouTrack.Tests", "Baselines", "dialog.palette.txt");
        ThemePaletteBaseline.Verify(baseline, painted);
    });

    /// <summary>
    /// Proves the harness is honest before any baseline built on it is believed (AC-337): the theme's text colour
    /// arrives through a selector, which only runs once a control reaches a shown window's styling root. A tree
    /// that is only measured, never shown, would still resolve its resource lookups and pass a plausible-looking
    /// but wrong report.
    /// </summary>
    [Fact]
    public void TheHarness_ShowsItsWindow_SoTheThemesSelectorsHaveRun() => HeadlessAvalonia.Run(() =>
    {
        var primary = (Color)(Application.Current?.FindResource("CockpitTextPrimaryColor")
            ?? throw new InvalidOperationException("The theme has no CockpitTextPrimaryColor."));

        var painted = _Painted();

        Assert.Contains(ThemePalette.Hex(primary), painted, StringComparison.Ordinal);
    });

    private static string _Painted()
    {
        var storage = new InMemoryPluginStorage();
        var settings = new YouTrackSettings(storage) { Instances = [LocalInstance] };
        var host = new FakeCockpitHost();
        var links = new SessionIssueLinks(host);
        var dialog = new YouTrackDialogControl(settings, host, links, new IssueStateChanges());

        var window = new Window { Width = 1280, Height = 860, Content = dialog };
        window.Show();
        window.UpdateLayout();

        // No fetch seam (the dialog talks to the YouTrack REST API directly), so the loaded set is planted the
        // same way YouTrackDialogControlTests does, then the grid is selected into so the detail panel also renders.
        var loaded = typeof(YouTrackDialogControl).GetField("_all", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("YouTrackDialogControl no longer keeps its loaded issues in _all.");
        loaded.SetValue(dialog, new[] { First, Second });

        var applyFilter = typeof(YouTrackDialogControl).GetMethod("_ApplyFilter", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("YouTrackDialogControl no longer has _ApplyFilter.");
        applyFilter.Invoke(dialog, null);

        var grid = window.GetVisualDescendants().OfType<DataGrid>().First();
        grid.SelectedItem = First;
        window.UpdateLayout();

        try
        {
            return ThemePalette.Describe(window);
        }
        finally
        {
            window.Close();
        }
    }

}
