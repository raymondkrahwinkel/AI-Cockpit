using Avalonia.Controls;
using Cockpit.App.Controls;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-937: pinned plugins stay directly in the sidebar, everything else collapses behind "Plugins ›" — and the
/// accent dot on that button follows a live badge without summing counters that are not the same unit.
/// </summary>
[Collection("avalonia")]
public class PluginsMenuTests
{
    [Fact]
    public void APinnedButton_IsInPinnedEntries_NotCollapsed() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = new CockpitViewModel();
        var sink = (IPluginContributionSink)cockpit;
        sink.AddPluginSideButton("autopilot", "Autopilot", () => { });
        sink.AddPluginSideButton("youtrack", "YouTrack", () => { });

        cockpit.ApplyPluginMenuPreference("autopilot", menuOrder: 0, hiddenInMenu: false, pinnedToSidebar: true);
        cockpit.ApplyPluginMenuPreference("youtrack", menuOrder: 1, hiddenInMenu: false, pinnedToSidebar: false);

        Assert.Equal(["autopilot"], cockpit.PinnedMenuEntries.Select(entry => entry.PluginId));
        Assert.Equal(["youtrack"], cockpit.CollapsedMenuEntries.Select(entry => entry.PluginId));
    });

    // #2 of the grooming's bouwvoorstel: a section is always drawn inline, never behind the flyout — an accordion in
    // a popup would be the wrong control, regardless of its plugin's pin.
    [Fact]
    public void ASection_IsAlwaysPinned_EvenWithoutAnExplicitPin() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = new CockpitViewModel();
        var sink = (IPluginContributionSink)cockpit;
        sink.AddPluginSideSection("github-pull-requests", "Open PRs", () => new TextBlock());

        Assert.Equal(["github-pull-requests"], cockpit.PinnedMenuEntries.Select(entry => entry.PluginId));
        Assert.Empty(cockpit.CollapsedMenuEntries);
    });

    [Fact]
    public void NoPluginPinned_EverythingCollapses() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = new CockpitViewModel();
        var sink = (IPluginContributionSink)cockpit;
        sink.AddPluginSideButton("workflows", "Workflows", () => { });

        Assert.Empty(cockpit.PinnedMenuEntries);
        Assert.Equal(["workflows"], cockpit.CollapsedMenuEntries.Select(entry => entry.PluginId));
    });

    public static IEnumerable<object[]> Dot() =>
    [
        // Nothing collapsed at all, and one collapsed badge with nothing on it: both leave the dot off.
        [Array.Empty<SideMenuButtonBadge>(), false],
        [new[] { new SideMenuButtonBadge() }, false],
        [new[] { new SideMenuButtonBadge(), new SideMenuButtonBadge { Primary = 3 } }, true],
        // Not a summed number: the dot is on/off only, whatever the counters say — 19 PR's + 3 issues never
        // becomes "22" anywhere in this control, which is what makes the row above and this one one behaviour.
        [new[] { new SideMenuButtonBadge { Primary = 19, Secondary = 0 }, new SideMenuButtonBadge { Primary = 3 } }, true],
    ];

    [Theory]
    [MemberData(nameof(Dot))]
    public void TheDot_FollowsWhetherAnyCollapsedBadgeHasSomethingToShow(SideMenuButtonBadge[] collapsed, bool expected) =>
        Assert.Equal(expected, PluginsMenuButton.ShouldShowDot(collapsed));
}
