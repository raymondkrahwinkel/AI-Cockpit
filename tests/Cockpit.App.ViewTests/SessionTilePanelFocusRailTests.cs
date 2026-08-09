using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Cockpit.App.Controls;
using Cockpit.Core.Shortcuts;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-444, on a render rather than on a claim: promoting a rail tile actually swaps which pane is drawn large
/// without rebuilding either container (the same instances before and after, AC-442's pty-preserving rule),
/// the rail actually orders itself attention-first-then-sidebar rather than by claim, and the attention/consent
/// signals actually paint at miniature scale rather than washing out. Screenshots land in
/// <see cref="OutputDirectory"/> for the eyeball half, the same convention AC-442/443 used.
/// </summary>
[Collection("avalonia")]
public class SessionTilePanelFocusRailTests
{
    public static readonly string OutputDirectory = Path.Combine(Path.GetTempPath(), "cockpit-ac444-focus-rail");

    [Fact]
    public void PromotingARailTile_SwapsTheFocusWithoutRebuildingEitherContainer() => HeadlessAvalonia.Run(() =>
    {
        var panel = new SessionTilePanel { FocusRailLayout = true };
        var a = _Pane("a", isFocus: true, sortKey: 0);
        var b = _Pane("b", isFocus: false, sortKey: 1);
        var c = _Pane("c", isFocus: false, sortKey: 2);
        panel.Children.Add(a);
        panel.Children.Add(b);
        panel.Children.Add(c);

        using var scene = RenderedScene.Show(panel, 900, 600);

        Assert.True(a.Bounds.Width > b.Bounds.Width, "the focus candidate must fill the large slot, not a rail tile");
        Assert.Equal(1.0, _MiniatureScaleOf(a));
        Assert.True(_MiniatureScaleOf(b) is > 0 and < 1, "a rail tile must draw smaller, never full scale");

        // Promote "b": the operator clicked it, which (via OnSessionPanePressed → SelectSessionCommand →
        // OnSelectedSessionChanged) flips IsSelected on the two panes — here, the attached property that
        // stands in for it.
        SessionTilePanel.SetIsFocusCandidate(a, false);
        SessionTilePanel.SetIsFocusCandidate(b, true);
        scene.Window.UpdateLayout();

        Assert.Same(a, panel.Children[0]);
        Assert.Same(b, panel.Children[1]);
        Assert.Same(c, panel.Children[2]);
        Assert.Equal(3, panel.Children.Count);

        Assert.True(b.Bounds.Width > a.Bounds.Width, "the promoted pane must now fill the large slot");
        Assert.Equal(1.0, _MiniatureScaleOf(b));
        Assert.True(_MiniatureScaleOf(a) is > 0 and < 1, "the demoted pane must go back to rail scale");
    });

    [Fact]
    public void RailOrder_PutsAttentionNeedingTilesFirstThenTheSidebarOrder() => HeadlessAvalonia.Run(() =>
    {
        var panel = new SessionTilePanel { FocusRailLayout = true };
        var focus = _Pane("focus", isFocus: true, sortKey: 0);
        // Sidebar positions 0, 1, 2 among the non-focus panes — "quiet2" sits earliest in the sidebar but
        // is not owed attention, so an attention-needing pane from later in the sidebar must still come first
        // (AC-444 #2: "sessies die aandacht vragen ... staan bovenaan de rail, daarna de sidebarvolgorde").
        var quiet1 = _Pane("quiet1", isFocus: false, sortKey: (1_000_000 * 1) + 1);
        var quiet2 = _Pane("quiet2", isFocus: false, sortKey: (1_000_000 * 1) + 0);
        var needsAttention = _Pane("attention", isFocus: false, sortKey: 2);
        panel.Children.Add(quiet1);
        panel.Children.Add(quiet2);
        panel.Children.Add(needsAttention);
        panel.Children.Add(focus);

        using var scene = RenderedScene.Show(panel, 900, 600);

        // Row-major fill: the earlier a tile sorts, the further left/up it sits.
        Assert.True(needsAttention.Bounds.Top < quiet2.Bounds.Top
            || (needsAttention.Bounds.Top == quiet2.Bounds.Top && needsAttention.Bounds.Left < quiet2.Bounds.Left),
            "the attention-needing pane must sort before every quiet pane, regardless of sidebar position");
        Assert.True(quiet2.Bounds.Top < quiet1.Bounds.Top
            || (quiet2.Bounds.Top == quiet1.Bounds.Top && quiet2.Bounds.Left < quiet1.Bounds.Left),
            "among quiet panes, the sidebar's own order decides");
    });

    [Fact]
    public void KeyboardNav_MovesBetweenFocusAndRailLikeTheGrid() => HeadlessAvalonia.Run(() =>
    {
        var panel = new SessionTilePanel { FocusRailLayout = true };
        var focus = _Pane("focus", isFocus: true, sortKey: 0);
        var r0 = _Pane("r0", isFocus: false, sortKey: 1);
        var r1 = _Pane("r1", isFocus: false, sortKey: 2);
        panel.Children.Add(focus);
        panel.Children.Add(r0);
        panel.Children.Add(r1);

        using var scene = RenderedScene.Show(panel, 900, 600);

        Assert.Equal("r0", panel.NeighbourInDirection("focus", PaneDirection.Right));
        Assert.Null(panel.NeighbourInDirection("focus", PaneDirection.Left));
        Assert.Equal("focus", panel.NeighbourInDirection("r0", PaneDirection.Left));
    });

    [Fact]
    public void AttentionBorderAndConsentScrim_StillPaintAtMiniatureScale() => HeadlessAvalonia.Run(() =>
    {
        Directory.CreateDirectory(OutputDirectory);

        // The pane template's own shape (CockpitView.axaml): the attention/active edge is a `Border` OUTSIDE
        // the `MiniatureHost` — deliberately, after this test first proved that a 1.5px edge scaled down
        // with everything else (`BorderThickness` *inside* the host) thins to a fraction of a device pixel
        // at rail scale and washes out to the background, which is exactly what AC-444 #3 forbids. Drawn at
        // its own real thickness here, same as the tile's outer edge always is regardless of the content
        // scale inside it.
        var attentionBrush = RenderedScene.TokenBrush("CockpitStatusWaitingBrush");
        var tile = new Border
        {
            Background = Brushes.Black,
            BorderBrush = attentionBrush,
            BorderThickness = new Thickness(1.5),
            Child = new MiniatureHost
            {
                Scale = 0.28,
                Child = new Border { Width = 1000, Height = 640, Background = Brushes.Black },
            },
        };

        using var scene = RenderedScene.Show(tile, (1000 * 0.28) + 3, (640 * 0.28) + 3);
        using (var frame = scene.Window.CaptureRenderedFrame())
        {
            frame!.Save(Path.Combine(OutputDirectory, "attention-border-at-rail-scale.png"));
        }

        var edgeColor = RenderedScene.PaintedAt(scene.Window, new Point(1, 1));
        var expected = RenderedScene.AsRendered(attentionBrush);
        Assert.True(_ColorsClose(edgeColor, expected),
            $"the attention edge painted {edgeColor} at rail scale, expected close to {expected} — it washed out");

        // The consent overlay (ConsentBannerHost): a #B3000000 scrim over the whole pane. At rail scale the
        // Approve/Deny text is unreadable by design (AC-441) — what has to survive is the scrim itself
        // reading as visibly darker than an un-scrimmed tile.
        var plainHost = new MiniatureHost
        {
            Scale = 0.28,
            Child = new Border { Width = 1000, Height = 640, Background = Brushes.White },
        };
        using var plainScene = RenderedScene.Show(plainHost, 1000 * 0.28, 640 * 0.28);
        var plainColor = RenderedScene.PaintedAt(plainScene.Window, new Point(50, 50));

        var scrimmedHost = new MiniatureHost
        {
            Scale = 0.28,
            Child = new Border
            {
                Width = 1000,
                Height = 640,
                Background = Brushes.White,
                Child = new Border { Background = new SolidColorBrush(Color.FromArgb(0xB3, 0, 0, 0)) },
            },
        };
        using var scrimmedScene = RenderedScene.Show(scrimmedHost, 1000 * 0.28, 640 * 0.28);
        using (var frame = scrimmedScene.Window.CaptureRenderedFrame())
        {
            frame!.Save(Path.Combine(OutputDirectory, "consent-scrim-at-rail-scale.png"));
        }

        var scrimmedColor = RenderedScene.PaintedAt(scrimmedScene.Window, new Point(50, 50));
        var plainLuma = plainColor.R + plainColor.G + plainColor.B;
        var scrimmedLuma = scrimmedColor.R + scrimmedColor.G + scrimmedColor.B;
        Assert.True(scrimmedLuma < plainLuma - 150,
            $"the consent scrim measured {scrimmedLuma} against an un-scrimmed {plainLuma} at rail scale — too close to tell a pending pane apart");
    });

    // A plain `Border` stands in for the real container (a `ContentPresenter`, generated by the production
    // `ItemsControl`): `SessionTilePanel` only ever reads a child's `DataContext` and its two attached
    // properties, never its concrete type. A bare `ContentPresenter` built by hand rather than by an
    // `ItemsControl` turned out not to hold a `DataContext` the way a real generated container does — a
    // `Border` sidesteps that without the test claiming anything about `ContentPresenter` it doesn't need to.
    private static Border _Pane(object key, bool isFocus, int sortKey)
    {
        var container = new Border { DataContext = key };
        SessionTilePanel.SetIsFocusCandidate(container, isFocus);
        SessionTilePanel.SetRailSortKey(container, sortKey);
        return container;
    }

    private static double _MiniatureScaleOf(Control container) => SessionTilePanel.GetMiniatureScale(container);

    private static bool _ColorsClose(Color a, Color b) =>
        Math.Abs(a.R - b.R) < 24 && Math.Abs(a.G - b.G) < 24 && Math.Abs(a.B - b.B) < 24;
}
