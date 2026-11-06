using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-670: the rail as a strip of miniatures, tested through the generated container the AC-441/444 suite skips.
/// </summary>
[Collection("avalonia")]
public class RailMiniatureTests
{
    /// <summary>
    /// AC-670 #7 against #1, which pull against each other: nothing inside a tile may take the click, yet the
    /// tile must still promote. Driven with a real pointer rather than the property that stands in for one.
    /// </summary>
    [Fact]
    public void ClickingARailTile_StillPromotesIt_WithNothingInsideTheTileTakingTheClick() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = new CockpitViewModel { GlobalFocusRailLayout = true };
        var view = new CockpitView { DataContext = cockpit };
        var window = new Window { Content = view, Width = 1400, Height = 900 };
        window.Show();
        window.UpdateLayout();

        var panel = view.GetLogicalDescendants().OfType<ItemsControl>().First(c => c.Name == "SessionGrid")
            .GetVisualDescendants().OfType<SessionTilePanel>().Single();
        var railTile = panel.Children
            .OfType<ContentPresenter>()
            .First(c => SessionTilePanel.GetIsMiniature(c));
        var promoted = Assert.IsAssignableFrom<SessionPanelViewModel>(railTile.DataContext);

        Assert.NotSame(promoted, cockpit.SelectedSession);
        Assert.All(railTile.GetVisualDescendants().OfType<MiniatureHost>(),
            host => Assert.False(host.IsHitTestVisible, "a tile's content must not be clickable"));

        var centre = railTile.TranslatePoint(new Point(railTile.Bounds.Width / 2, railTile.Bounds.Height / 2), window);
        Assert.NotNull(centre);
        window.MouseDown(centre!.Value, MouseButton.Left);
        window.MouseUp(centre.Value, MouseButton.Left);
        window.UpdateLayout();

        Assert.Same(promoted, cockpit.SelectedSession);
    });
}
