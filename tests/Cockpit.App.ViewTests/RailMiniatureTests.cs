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
/// AC-670: the rail is a strip of miniatures, one under the other. Two facts the rest of the rail leans on and
/// neither of which the AC-441/444 suite could see, because those tests put plain controls straight into the
/// panel while the real cockpit puts a generated container between the panel and the pane.
/// </summary>
[Collection("avalonia")]
public class RailMiniatureTests
{
    /// <summary>
    /// The scale the panel writes lands on its own child — the ItemsControl's container — while the markup that
    /// consumes it (<c>MiniatureHost.Scale</c>, bound to <c>#PaneRoot</c>) sits one level down inside that
    /// container's template. Without an inherited property the Border keeps the 1.0 default: the rail then lays
    /// each pane out at tile size instead of drawing it small, which reflows the pane and resizes its pty.
    /// </summary>
    [Fact]
    public void TheScaleWrittenOnTheContainer_ReachesTheTemplateRootInsideIt() => HeadlessAvalonia.Run(() =>
    {
        var paneRoot = new Border();
        var container = new ContentPresenter { Content = paneRoot };
        using var scene = RenderedScene.Show(container, 400, 300);

        SessionTilePanel.SetMiniatureScale(container, 0.25);
        scene.Window.UpdateLayout();

        Assert.Equal(0.25, SessionTilePanel.GetMiniatureScale(paneRoot));
        Assert.True(SessionTilePanel.GetIsMiniature(paneRoot), "anything drawn below full size is a miniature");
    });

    /// <summary>
    /// One column at every rail width. 1600px wide gives a rail of roughly 460px at the default weight — well
    /// past the 320px at which the rail used to fold into a second column.
    /// </summary>
    [Theory]
    [InlineData(700)]
    [InlineData(1600)]
    [InlineData(2600)]
    public void TheRailStacksInOneColumn_AtEveryWidth(double windowWidth) => HeadlessAvalonia.Run(() =>
    {
        var panel = new SessionTilePanel { FocusRailLayout = true };
        var focus = _Pane("focus", isFocus: true, sortKey: 0);
        var tiles = new[] { _Pane("a", false, 1), _Pane("b", false, 2), _Pane("c", false, 3) };
        panel.Children.Add(focus);
        foreach (var tile in tiles)
        {
            panel.Children.Add(tile);
        }

        using var scene = RenderedScene.Show(panel, windowWidth, 900);

        Assert.All(tiles, tile => Assert.Equal(tiles[0].Bounds.Left, tile.Bounds.Left, precision: 3));
        Assert.True(tiles[0].Bounds.Top < tiles[1].Bounds.Top && tiles[1].Bounds.Top < tiles[2].Bounds.Top,
            $"at {windowWidth}px the tiles must stack top to bottom, not wrap into a second column");
    });

    /// <summary>
    /// AC-670 #7 against AC-670 #1, on the real view: nothing inside a tile takes the click any more
    /// (<c>MiniatureHost</c> is hit-test-invisible, so a press can't land in another session's composer), and the
    /// tile still promotes. Those two pull against each other — a press that reaches nothing at all promotes
    /// nothing either — so this drives an actual pointer rather than the attached property that stands in for one.
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

    private static Border _Pane(object key, bool isFocus, int sortKey)
    {
        var container = new Border { DataContext = key };
        SessionTilePanel.SetIsFocusCandidate(container, isFocus);
        SessionTilePanel.SetRailSortKey(container, sortKey);
        return container;
    }
}
