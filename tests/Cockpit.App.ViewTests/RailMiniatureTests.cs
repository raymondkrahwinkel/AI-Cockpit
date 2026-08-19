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
using Exclr8.Terminal;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-670: the rail as a strip of miniatures, tested through the generated container the AC-441/444 suite skips.
/// </summary>
[Collection("avalonia")]
public class RailMiniatureTests
{
    /// <summary>
    /// The panel writes onto the container; the markup reads one level down, inside that container's template.
    /// Without inheritance the boxes never arrive and a tile is laid out small rather than drawn small.
    /// </summary>
    [Fact]
    public void TheBoxesWrittenOnTheContainer_ReachTheTemplateRootInsideIt() => HeadlessAvalonia.Run(() =>
    {
        var paneRoot = new Border();
        var container = new ContentPresenter { Content = paneRoot };
        using var scene = RenderedScene.Show(container, 400, 300);

        SessionTilePanel.SetMiniatureBox(container, new Size(100, 64), new Size(400, 256));
        scene.Window.UpdateLayout();

        Assert.Equal(new Size(100, 64), SessionTilePanel.GetMiniatureTileSize(paneRoot));
        Assert.Equal(new Size(400, 256), SessionTilePanel.GetMiniatureFocusSize(paneRoot));
        Assert.True(SessionTilePanel.GetIsMiniature(paneRoot), "a pane with a focus box to shrink from is a tile");
    });

    /// <summary>
    /// The arithmetic the pty depends on, without a terminal in the way: the child gets the focus box minus the
    /// pane's own chrome, in both states. Dividing by a scale instead multiplies that chrome by 1/scale.
    /// </summary>
    [Theory]
    [InlineData(252, 197)]   // the rail at its default weight
    [InlineData(160, 125)]   // dragged to the minimum
    [InlineData(600, 469)]   // dragged wide
    public void TheChildIsLaidOutInTheFocusBoxMinusTheSameChrome(double tileWidth, double tileHeight)
    {
        const double inset = 10;    // PaneRoot: 4px margin either side, plus a 1px border
        var focus = new Size(809, 632);
        var tile = new Size(tileWidth, tileHeight);
        var hostBox = new Size(tileWidth - inset, tileHeight - inset);

        var (railBox, railScale) = MiniatureHost.Fit(hostBox, tile, focus);
        var (focusBox, focusScale) = MiniatureHost.Fit(
            new Size(focus.Width - inset, focus.Height - inset), focus, focus);

        Assert.Equal(focusBox, railBox);
        Assert.Equal(1.0, focusScale);
        Assert.True(railScale is > 0 and < 1, $"a tile must draw smaller, got {railScale}");

        // And it fits the tile it was given: the drawn width is the host's box, not something clipped.
        Assert.Equal(hostBox.Width, railBox.Width * railScale, precision: 6);
    }

    /// <summary>Outside a rail there is no focus box, and the host is a pass-through at scale 1.</summary>
    [Fact]
    public void WithoutAFocusBox_TheHostIsTheIdentity()
    {
        var (box, scale) = MiniatureHost.Fit(new Size(800, 600), default, default);

        Assert.Equal(new Size(800, 600), box);
        Assert.Equal(1.0, scale);
    }

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

    /// <summary>
    /// AC-442's invariant through the real view, where the two things an isolated host cannot see show up: the
    /// pane's chrome between container and host, and rail chrome that collapses or docks inside the scaled subtree.
    /// </summary>
    // AC-923: checked at a second window size too, not just 1400x900 — the rounding mismatch this guards
    // against depends on where a fractional pixel happens to land relative to a terminal cell boundary.
    [Theory]
    [InlineData(1400, 900)]
    [InlineData(1800, 1000)]
    public async Task ATtyPaneInTheRail_KeepsTheGridItHasInFocus(double windowWidth, double windowHeight)
    {
        (int Cols, int Rows) asTile = default, asFocus = default;
        var resizes = 0;

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var cockpit = new CockpitViewModel { GlobalFocusRailLayout = true };
            var view = new CockpitView { DataContext = cockpit };
            var window = new Window { Content = view, Width = windowWidth, Height = windowHeight };
            window.Show();
            window.UpdateLayout();

            var terminal = view.GetVisualDescendants().OfType<TerminalControl>().Single();
            await TerminalSettle.WaitAsync(terminal);
            var tty = view.GetVisualDescendants().OfType<TtyView>().Single();
            var container = tty.GetVisualAncestors().OfType<ContentPresenter>()
                .First(c => c.DataContext is SessionPanelViewModel);

            Assert.True(SessionTilePanel.GetIsMiniature(container), "the TTY pane must start as a rail tile");
            asTile = (terminal.Buffer.Cols, terminal.Buffer.Rows);
            terminal.Resized += (_, _) => resizes++;

            // Promote it: the tile becomes the focus pane, at full size and with all its chrome back.
            cockpit.SelectSessionCommand.Execute(container.DataContext);
            window.UpdateLayout();
            await TerminalSettle.WaitAsync(terminal);

            Assert.False(SessionTilePanel.GetIsMiniature(container), "the promoted pane must have left the rail");
            asFocus = (terminal.Buffer.Cols, terminal.Buffer.Rows);
        });

        Assert.NotEqual((80, 24), asTile);
        Assert.Equal(asTile, asFocus);
        Assert.Equal(0, resizes);
    }

    private static Border _Pane(object key, bool isFocus, int sortKey)
    {
        var container = new Border { DataContext = key };
        SessionTilePanel.SetIsFocusCandidate(container, isFocus);
        SessionTilePanel.SetRailSortKey(container, sortKey);
        return container;
    }
}
