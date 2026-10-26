using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1763: what a plugin adds to the sidebar lands in one card per plugin; plugins with a single button share the
/// "Plugins" card, whose folded header carries the sum of the numeric badges.
/// </summary>
[Collection("avalonia")]
public class PluginsMenuTests
{
    [Fact]
    public void ButtonsAndSections_GroupPerPlugin_AndSingleButtonPluginsShareOneCard() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = new CockpitViewModel();
        var sink = (IPluginContributionSink)cockpit;
        sink.AddPluginSideButton("productivity", "Uren", () => { });
        sink.AddPluginSideButton("github-pull-requests", "Open PRs", () => { }, new SideMenuButtonBadge { Primary = 19, Secondary = 0 });
        sink.AddPluginSideButton("productivity", "Overzicht", () => { });
        sink.AddPluginSideSection("productivity", "Tijd", () => new TextBlock());
        sink.AddPluginSideButton("github-issues", "GitHub Issues", () => { }, new SideMenuButtonBadge { Primary = 3 });

        var groups = cockpit.PluginMenuGroups;

        Assert.Equal(["plugin:productivity", "plugins"], groups.Select(group => group.Key));
        Assert.Equal(["Uren", "Overzicht"], groups[0].Buttons.Select(button => button.Title));
        Assert.Equal(["Tijd"], groups[0].Sections.Select(section => section.Title));
        Assert.Equal(["Open PRs", "GitHub Issues"], groups[1].Buttons.Select(button => button.Title));
        Assert.Equal(22, groups[1].BadgeTotal);
    });
}
