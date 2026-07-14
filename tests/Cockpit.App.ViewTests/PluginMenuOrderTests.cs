using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using FluentAssertions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The order the operator gave the left menu (#72) applies to everything the plugins put in it. Drawing every button
/// and then every section meant a plugin contributing a section (the open pull requests) sat below every plugin
/// contributing a button, however far up the operator moved it — an order that a plugin's kind can overrule is not an
/// order, and the setting that changes it reads as broken.
/// <para>
/// Here rather than in the unit tests because a contribution is registered on the UI thread: without one, the
/// registration is posted to a dispatcher nobody pumps and the menu comes back empty.
/// </para>
/// </summary>
[Collection("avalonia")]
public class PluginMenuOrderTests
{
    [Fact]
    public void ASectionMovedToTheTop_IsAtTheTop_AboveTheButtons() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = new CockpitViewModel();
        var sink = (IPluginContributionSink)cockpit;
        sink.AddPluginSideButton("youtrack", "YouTrack", () => { });
        sink.AddPluginSideSection("github-pull-requests", "Open PRs", () => new TextBlock());
        sink.AddPluginSideButton("workflows", "Workflows", () => { });

        cockpit.ApplyPluginMenuPreference("github-pull-requests", menuOrder: 0, hiddenInMenu: false);
        cockpit.ApplyPluginMenuPreference("workflows", menuOrder: 1, hiddenInMenu: false);
        cockpit.ApplyPluginMenuPreference("youtrack", menuOrder: 2, hiddenInMenu: false);

        cockpit.VisibleMenuEntries.Select(entry => entry.PluginId)
            .Should().Equal("github-pull-requests", "workflows", "youtrack");
    });

    [Fact]
    public void APluginHiddenFromTheMenu_ContributesNothingToIt() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = new CockpitViewModel();
        var sink = (IPluginContributionSink)cockpit;
        sink.AddPluginSideButton("youtrack", "YouTrack", () => { });
        sink.AddPluginSideSection("transcript-search", "Search", () => new TextBlock());

        cockpit.ApplyPluginMenuPreference("transcript-search", menuOrder: 0, hiddenInMenu: true);

        cockpit.VisibleMenuEntries.Select(entry => entry.PluginId).Should().Equal("youtrack");
    });

    // A plugin that contributes both keeps its launcher above its own section: the button is how you reach it, the
    // section is what it has to say.
    [Fact]
    public void APluginWithBothAButtonAndASection_KeepsTheButtonAbove() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = new CockpitViewModel();
        var sink = (IPluginContributionSink)cockpit;
        sink.AddPluginSideSection("github-pull-requests", "Open PRs", () => new TextBlock());
        sink.AddPluginSideButton("github-pull-requests", "Pull requests", () => { });

        cockpit.VisibleMenuEntries.Select(entry => entry.Button is not null).Should().Equal(true, false);
    });
}
