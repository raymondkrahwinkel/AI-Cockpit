using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions.UI;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// Reaching a plugin's settings from where the operator already is (#: settings from anywhere): the plugin can
/// open its own settings (<see cref="ICockpitUiHost.ShowSettingsAsync"/>), and every dialog it opens carries a
/// gear to them — but only when the plugin actually registered a settings view, since a gear that opens nothing
/// is exactly the dead control the cockpit does not ship.
/// </summary>
public class PluginSettingsAccessTests
{
    [Fact]
    public void ShowSettings_OpensThisPluginsOwnSettings()
    {
        var sink = Substitute.For<IPluginContributionSink>();
        var host = NewHost(sink);

        _ = host.ShowSettingsAsync();

        sink.Received(1).OpenPluginSettingsAsync("test-plugin");
    }

    [Fact]
    public void AddSettings_RegistersUnderThePluginsName_SoEveryGearTitlesTheDialogTheSameWay()
    {
        var sink = Substitute.For<IPluginContributionSink>();
        var host = NewHost(sink);

        host.AddSettings(() => new TextBlock());

        sink.Received(1).AddPluginSettings("test-plugin", "Test Plugin", Arg.Any<Func<Control>>());
    }

    // AC-1030: a plugin can declare which Options sidebar group its settings row lands in.
    [Fact]
    public void AddSettings_WithACategory_RegistersItAlongsideTheView()
    {
        var sink = Substitute.For<IPluginContributionSink>();
        var host = NewHost(sink);

        host.AddSettings(() => new TextBlock(), "Assistant Plugins");

        sink.Received(1).AddPluginSettings("test-plugin", "Test Plugin", Arg.Any<Func<Control>>(), "Assistant Plugins");
    }

    [Fact]
    public void ADialogFromAPluginWithSettings_CarriesAGearThatOpensThem()
    {
        var sink = Substitute.For<IPluginContributionSink>();
        sink.HasPluginSettings("test-plugin").Returns(true);
        var dialogHost = Substitute.For<IPluginDialogHost>();
        var host = NewHost(sink, dialogHost);

        _ = host.ShowDialogAsync("Issues", () => new TextBlock());

        var onOpenSettings = (Func<Task>?)dialogHost.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IPluginDialogHost.ShowDialogAsync))
            .GetArguments()[4];
        Assert.NotNull(onOpenSettings);

        _ = onOpenSettings!();

        sink.Received(1).OpenPluginSettingsAsync("test-plugin");
    }

    // The gear is only there when it leads somewhere: a plugin with no settings view would otherwise show one
    // that opens nothing at all.
    [Fact]
    public void ADialogFromAPluginWithoutSettings_HasNoGear()
    {
        var sink = Substitute.For<IPluginContributionSink>();
        sink.HasPluginSettings("test-plugin").Returns(false);
        var dialogHost = Substitute.For<IPluginDialogHost>();
        var host = NewHost(sink, dialogHost);

        _ = host.ShowDialogAsync("Issues", () => new TextBlock());

        var onOpenSettings = dialogHost.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IPluginDialogHost.ShowDialogAsync))
            .GetArguments()[4];
        Assert.Null(onOpenSettings);
    }

    [Fact]
    public void HasSettings_ReportsWhetherThePluginRegisteredAView()
    {
        var sink = Substitute.For<IPluginContributionSink>();
        sink.HasPluginSettings("test-plugin").Returns(true);

        Assert.True(NewHost(sink).HasSettings);
    }

    private static ICockpitUiHost NewHost(IPluginContributionSink sink, IPluginDialogHost? dialogHost = null) =>
        TestUiHost.Create(sink: sink, dialogHost: dialogHost);
}
