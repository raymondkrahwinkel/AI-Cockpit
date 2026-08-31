using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.App.Controls;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1264: <see cref="SessionTilePanel"/> hands its rail tiles the box the focus pane's own host was arranged
/// into, and <c>MiniatureHost.FocusChildBoxProperty</c> is registered <c>AffectsMeasure</c> — written from an
/// arrange, that asks the running pass for a measure off a value that pass produced itself. Both halves are
/// pinned: the box never arrives inside an arrange, and a tile is still measured at the focus pane's exact box
/// rather than the approximation dropping <c>AffectsMeasure</c> would have left it with.
/// </summary>
[Collection("avalonia")]
public class Ac1264FocusChildBoxTests
{
    [Fact]
    public void TheFocusBox_NeverArrivesOnATileFromInsideAnArrange() => HeadlessAvalonia.Run(() =>
    {
        var panel = _RailPanel(out var focusHost, out var tile, out _);

        using var scene = RenderedScene.Show(panel, 900, 600);
        _Settle(scene);

        Assert.True(tile.BoxWrites > 0, "the tile must have been told the focus box at all");
        Assert.Equal(0, tile.BoxWritesInsideArrange);
        Assert.True(focusHost.Bounds.Width > 0, "the focus pane's host must have been arranged into a real box");
        LayoutSettledAssertion.AssertSettled(panel);
    });

    [Fact]
    public void ARailTilesChild_IsMeasuredAtTheFocusPanesOwnBox() => HeadlessAvalonia.Run(() =>
    {
        var panel = _RailPanel(out var focusHost, out _, out var railContent);

        using var scene = RenderedScene.Show(panel, 900, 600);
        _Settle(scene);

        // `MiniatureHost.Fit` measures the child at the focus box itself, and a TTY pane's pty follows that
        // constraint (AC-442/670) — so a box the host was never re-measured for is a wrong terminal, not a
        // cosmetic one.
        Assert.Equal(focusHost.Bounds.Size, railContent.LastConstraint);
    });

    // One full layout, then whatever the arrange asked for from outside it, then the layout that answer needs.
    private static void _Settle(RenderedScene.Scene scene)
    {
        scene.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        scene.Window.UpdateLayout();
    }

    // The panel with one focus pane and one rail tile, shaped like `CockpitView.axaml`: a container the panel
    // writes its attached boxes onto, with a `MiniatureHost` inside reading them back through the same bindings.
    private static SessionTilePanel _RailPanel(out MiniatureHost focusHost, out WatchedTile tile, out MeasuredChild railContent)
    {
        var panel = new SessionTilePanel { FocusRailLayout = true };

        focusHost = new MiniatureHost { Child = new MeasuredChild() };
        var focus = new Grid { Margin = new Thickness(4), Background = Brushes.Transparent };
        focus.Children.Add(focusHost);
        _BindHost(focusHost, focus);
        SessionTilePanel.SetIsFocusCandidate(focus, true);

        railContent = new MeasuredChild();
        var railHost = new MiniatureHost { Child = railContent };
        tile = new WatchedTile { Margin = new Thickness(4), Child = railHost };
        _BindHost(railHost, tile);
        SessionTilePanel.SetIsFocusCandidate(tile, false);

        panel.Children.Add(focus);
        panel.Children.Add(tile);
        return panel;
    }

    private static void _BindHost(MiniatureHost host, Control paneRoot)
    {
        host.Bind(MiniatureHost.TileSizeProperty, paneRoot.GetObservable(SessionTilePanel.MiniatureTileSizeProperty));
        host.Bind(MiniatureHost.FocusSizeProperty, paneRoot.GetObservable(SessionTilePanel.MiniatureFocusSizeProperty));
        host.Bind(MiniatureHost.FocusChildBoxProperty, paneRoot.GetObservable(SessionTilePanel.MiniatureFocusChildBoxProperty));
    }

    /// <summary>
    /// A rail tile that notices a focus box landing on it between its own measure and its own arrange — the
    /// window the panel's arrange pass is the only thing running in, so a write seen there came from one.
    /// </summary>
    internal sealed class WatchedTile : Decorator
    {
        private int _writesAtMeasure;

        static WatchedTile() =>
            SessionTilePanel.MiniatureFocusChildBoxProperty.Changed.AddClassHandler<WatchedTile>((tile, _) => tile.BoxWrites++);

        public int BoxWrites { get; private set; }

        public int BoxWritesInsideArrange { get; private set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            _writesAtMeasure = BoxWrites;
            return base.MeasureOverride(availableSize);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (BoxWrites > _writesAtMeasure)
            {
                BoxWritesInsideArrange++;
            }

            return base.ArrangeOverride(finalSize);
        }
    }

    /// <summary>What a pane's content was last measured at — a TTY pane's pty is sized off exactly this.</summary>
    internal sealed class MeasuredChild : Control
    {
        public Size LastConstraint { get; private set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            LastConstraint = availableSize;
            return new Size(400, 300);
        }
    }
}
