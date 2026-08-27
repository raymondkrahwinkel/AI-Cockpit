using Avalonia;
using Avalonia.Controls;
using Cockpit.App.Controls;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1126: the five controls AC-1120 found with a layout override, each put through one real
/// measure/arrange cycle and checked with the same assertion — the whole population, not a sample.
/// </summary>
[Collection("avalonia")]
public class LayoutOverrideSettlesTests
{
    [Fact]
    public void FocusRailPanel_SettlesAfterOneLayout() => HeadlessAvalonia.Run(() =>
    {
        var panel = new FocusRailPanel();
        panel.Children.Add(new Border());
        panel.Children.Add(new Border());

        using var scene = RenderedScene.Show(panel, 900, 600);

        LayoutSettledAssertion.AssertSettled(panel);
    });

    [Fact]
    public void RailTilePanel_SettlesAfterOneLayout() => HeadlessAvalonia.Run(() =>
    {
        var panel = new RailTilePanel();
        panel.Children.Add(new Border());
        panel.Children.Add(new Border());

        using var scene = RenderedScene.Show(panel, 300, 400);

        LayoutSettledAssertion.AssertSettled(panel);
    });

    [Fact]
    public void MiniatureHost_SettlesAfterOneLayout() => HeadlessAvalonia.Run(() =>
    {
        var host = new MiniatureHost
        {
            TileSize = new Size(280, 180),
            FocusSize = new Size(1000, 640),
            Child = new Border { Width = 1000, Height = 640 },
        };

        using var scene = RenderedScene.Show(host, 280, 180);

        LayoutSettledAssertion.AssertSettled(host);
    });

    [Fact]
    public void LimitBar_SettlesAfterOneLayout() => HeadlessAvalonia.Run(() =>
    {
        var bar = new LimitBar { Label = "ctx", Percent = 42 };

        using var scene = RenderedScene.Show(bar, 200, 40);

        LayoutSettledAssertion.AssertSettled(bar);
    });

    [Fact]
    public void SessionTilePanel_FocusRailArrange_SettlesAfterOneLayout() => HeadlessAvalonia.Run(() =>
    {
        var panel = new SessionTilePanel { FocusRailLayout = true };

        var focus = new Border { Child = new MiniatureHost() };
        SessionTilePanel.SetIsFocusCandidate(focus, true);

        var rail = new MiniatureHost();
        SessionTilePanel.SetIsFocusCandidate(rail, false);
        rail.Bind(MiniatureHost.FocusChildBoxProperty, rail.GetObservable(SessionTilePanel.MiniatureFocusChildBoxProperty));

        panel.Children.Add(focus);
        panel.Children.Add(rail);

        using var scene = RenderedScene.Show(panel, 900, 600);

        LayoutSettledAssertion.AssertSettled(panel);
    });
}
