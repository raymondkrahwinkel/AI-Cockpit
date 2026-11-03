using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;

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
    public void APluginHiddenFromTheMenu_ContributesNothingToIt() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = new CockpitViewModel();
        var sink = (IPluginContributionSink)cockpit;
        sink.AddPluginSideButton("youtrack", "YouTrack", () => { });
        sink.AddPluginSideSection("transcript-search", "Search", () => new TextBlock());

        cockpit.ApplyPluginMenuPreference("transcript-search", menuOrder: 0, hiddenInMenu: true);

        Assert.Equal(new[] { "youtrack" }, cockpit.VisibleMenuEntries.Select(entry => entry.PluginId));
    });
}
