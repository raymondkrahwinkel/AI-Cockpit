using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions;
using FluentAssertions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The Sessions-toolbar actions plugins contribute (AC-91) obey the same order/visibility the operator gave the left
/// menu (#72): a plugin hidden there surfaces no toolbar button, and the order carries across. Here rather than in the
/// unit tests because a contribution is registered on the UI thread — without one the registration is posted to a
/// dispatcher nobody pumps and the collection comes back empty.
/// </summary>
[Collection("avalonia")]
public class PluginToolbarActionTests
{
    private static ToolbarAction _Action(string title) => new(title, null, () => Task.CompletedTask);

    [Fact]
    public void VisibleToolbarActions_FollowTheOperatorsMenuOrder() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = new CockpitViewModel();
        var sink = (IPluginContributionSink)cockpit;
        sink.AddToolbarAction("docker", _Action("Docker settings"));
        sink.AddToolbarAction("kubernetes", _Action("Kubernetes settings"));

        cockpit.ApplyPluginMenuPreference("kubernetes", menuOrder: 0, hiddenInMenu: false);
        cockpit.ApplyPluginMenuPreference("docker", menuOrder: 1, hiddenInMenu: false);

        cockpit.VisibleToolbarActions.Select(action => action.PluginId).Should().Equal("kubernetes", "docker");
    });

    [Fact]
    public void APluginHiddenFromTheMenu_ContributesNoToolbarAction() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = new CockpitViewModel();
        var sink = (IPluginContributionSink)cockpit;
        sink.AddToolbarAction("docker", _Action("Docker settings"));
        sink.AddToolbarAction("kubernetes", _Action("Kubernetes settings"));

        cockpit.ApplyPluginMenuPreference("docker", menuOrder: 0, hiddenInMenu: true);

        cockpit.VisibleToolbarActions.Select(action => action.PluginId).Should().Equal("kubernetes");
    });
}
