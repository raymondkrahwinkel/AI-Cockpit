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
/// AC-670: the rail is a strip of miniatures, one under the other. Two facts the rest of the rail leans on and
/// neither of which the AC-441/444 suite could see, because those tests put plain controls straight into the
/// panel while the real cockpit puts a generated container between the panel and the pane.
/// </summary>
[Collection("avalonia")]
public class RailMiniatureTests
{
    /// <summary>
    /// The boxes the panel writes land on its own child — the ItemsControl's container — while the markup that
    /// consumes them (<c>MiniatureHost.TileSize</c>/<c>FocusSize</c>, bound to <c>#PaneRoot</c>) sits one level
    /// down inside that container's template. Without inherited properties the Border keeps the empty default:
    /// the rail then lays each pane out at tile size instead of drawing it small, reflowing the pane and
    /// resizing its pty.
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
    /// The arithmetic the pty depends on, without a terminal in the way. The inset is the pane's own chrome
    /// (<c>PaneRoot</c>'s margin and border): the child has to be laid out in the focus box minus that same
    /// chrome, in both states, or the terminal inside changes shape between the rail and the focus slot.
    /// Dividing the host's box by a scale computed from container widths — what this replaced — takes the inset
    /// off first and then multiplies it by 1/scale, which is where three columns went missing.
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

    /// <summary>
    /// AC-442's invariant through the real view rather than <c>MiniatureHost</c> in isolation, which is the only
    /// place two things it cannot see show up: the pane's own chrome sitting between the container and the host
    /// (AC-670 — that cost three columns), and any rail chrome that collapses or docks inside the scaled subtree
    /// (measured while building this: taking TtyView's header out of the layout moved a 1000x640 pane from 39
    /// rows to 41, which is why that bar is blanked rather than collapsed and why the identity strip is an
    /// overlay rather than a dock). Fails on any of those coming back.
    /// </summary>
    [Fact]
    public async Task ATtyPaneInTheRail_KeepsTheGridItHasInFocus()
    {
        (int Cols, int Rows) asTile = default, asFocus = default;
        var resizes = 0;

        await HeadlessAvalonia.RunAsync(async () =>
        {
            var cockpit = new CockpitViewModel { GlobalFocusRailLayout = true };
            var view = new CockpitView { DataContext = cockpit };
            var window = new Window { Content = view, Width = 1400, Height = 900 };
            window.Show();
            window.UpdateLayout();
            await Task.Delay(300);

            var terminal = view.GetVisualDescendants().OfType<TerminalControl>().Single();
            var tty = view.GetVisualDescendants().OfType<TtyView>().Single();
            var container = tty.GetVisualAncestors().OfType<ContentPresenter>()
                .First(c => c.DataContext is SessionPanelViewModel);

            Assert.True(SessionTilePanel.GetIsMiniature(container), "the TTY pane must start as a rail tile");
            asTile = (terminal.Buffer.Cols, terminal.Buffer.Rows);
            terminal.Resized += (_, _) => resizes++;

            // Promote it: the tile becomes the focus pane, at full size and with all its chrome back.
            cockpit.SelectSessionCommand.Execute(container.DataContext);
            window.UpdateLayout();
            await Task.Delay(300);

            Assert.False(SessionTilePanel.GetIsMiniature(container), "the promoted pane must have left the rail");
            asFocus = (terminal.Buffer.Cols, terminal.Buffer.Rows);
        });

        Assert.True(asTile.Rows > 0, "the harness measured no grid, so it proves nothing");
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
