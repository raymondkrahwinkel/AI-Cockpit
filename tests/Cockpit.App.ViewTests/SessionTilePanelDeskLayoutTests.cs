using Avalonia.Controls;
using Cockpit.App.Controls;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-696, on arranged bounds rather than on a claim: the grid sizes itself from the panes of the workspace tab
/// now showing, not from every session alive. The panel holds a container for all of them — the other desks'
/// included, because rebinding to a filtered list would rebuild the panes and strand their ptys (AC-442) — so
/// a two-pane tab used to lay out as a 2x2, leaving an empty row under it while a third session ran elsewhere.
/// </summary>
[Collection("avalonia")]
public class SessionTilePanelDeskLayoutTests
{
    [Fact]
    public void APaneOnAnotherDesk_DoesNotAddARowToThisTabsGrid() => HeadlessAvalonia.Run(() =>
    {
        var panel = new SessionTilePanel();
        var here1 = _Pane("here1", onActiveDesk: true);
        var here2 = _Pane("here2", onActiveDesk: true);
        var elsewhere = _Pane("elsewhere", onActiveDesk: false);
        panel.Children.Add(here1);
        panel.Children.Add(here2);
        panel.Children.Add(elsewhere);

        using var scene = RenderedScene.Show(panel, 900, 600);

        Assert.Equal(panel.Bounds.Height, here1.Bounds.Height, 1);
        Assert.Equal(panel.Bounds.Height, here2.Bounds.Height, 1);
        Assert.Equal(here1.Bounds.Top, here2.Bounds.Top, 1);
        Assert.True(here1.Bounds.Left < here2.Bounds.Left, "two panes on one desk tile side by side");
    });

    [Fact]
    public void ThreePanesOnTheOneDesk_StillFormTheTwoByTwo() => HeadlessAvalonia.Run(() =>
    {
        var panel = new SessionTilePanel();
        var a = _Pane("a", onActiveDesk: true);
        var b = _Pane("b", onActiveDesk: true);
        var c = _Pane("c", onActiveDesk: true);
        panel.Children.Add(a);
        panel.Children.Add(b);
        panel.Children.Add(c);

        using var scene = RenderedScene.Show(panel, 900, 600);

        Assert.True(a.Bounds.Height < panel.Bounds.Height / 2 + 1, "3 panes keep growing downwards into a 2x2");
        Assert.True(c.Bounds.Top > a.Bounds.Top, "the third pane opens the second row");
    });

    [Fact]
    public void SwitchingDesks_RelaysTheGridForTheTabThatBecameActive() => HeadlessAvalonia.Run(() =>
    {
        var panel = new SessionTilePanel();
        var deskA = _Pane("a", onActiveDesk: true);
        var deskB1 = _Pane("b1", onActiveDesk: false);
        var deskB2 = _Pane("b2", onActiveDesk: false);
        panel.Children.Add(deskA);
        panel.Children.Add(deskB1);
        panel.Children.Add(deskB2);

        using var scene = RenderedScene.Show(panel, 900, 600);

        // Desk B becomes the tab showing: its two panes take over, desk A's single pane hides.
        _Switch(deskA, onActiveDesk: false);
        _Switch(deskB1, onActiveDesk: true);
        _Switch(deskB2, onActiveDesk: true);
        scene.Window.UpdateLayout();

        Assert.Equal(panel.Bounds.Height, deskB1.Bounds.Height, 1);
        Assert.Equal(panel.Bounds.Height, deskB2.Bounds.Height, 1);
        Assert.True(deskB1.Bounds.Left < deskB2.Bounds.Left, "desk B's own two panes tile side by side");
    });

    // Stands in for the real container the same way `SessionTilePanelFocusRailTests._Pane` does: the panel
    // reads only `DataContext` and its attached properties, never the concrete type. A pane on another desk is
    // hidden as well as off-desk, which is exactly what `CockpitViewModel.RefreshPaneVisibility` sets.
    private static Border _Pane(object key, bool onActiveDesk)
    {
        var container = new Border { DataContext = key, IsVisible = onActiveDesk };
        SessionTilePanel.SetIsOnActiveDesk(container, onActiveDesk);
        return container;
    }

    private static void _Switch(Border pane, bool onActiveDesk)
    {
        pane.IsVisible = onActiveDesk;
        SessionTilePanel.SetIsOnActiveDesk(pane, onActiveDesk);
    }
}
