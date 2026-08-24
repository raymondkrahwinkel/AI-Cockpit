using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// Reaching a plugin's settings from where the operator already is (#: settings from anywhere): the plugin can
/// open its own settings (<see cref="ICockpitHost.ShowSettingsAsync"/>), and every dialog it opens carries a
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

    // AC-1030: a plugin binary compiled before the category overload existed only implements the one-arg
    // AddSettings — the interface default keeps it loading by falling back to that.
    [Fact]
    public void AddSettings_WithACategory_OnAnOlderHost_FallsBackToTheCategorylessOverload()
    {
        var host = (ICockpitHost)Substitute.ForPartsOf<HostWithoutSettingsAccess>();

        // Not throwing is the assertion (xUnit fails the test on an unhandled exception).
        host.AddSettings(() => new TextBlock(), "Assistant Plugins");
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

    // The default is a no-op, so a plugin built against this SDK still loads on a host that predates the
    // capability instead of failing when it asks for its settings.
    [Fact]
    public async Task AHostWithoutTheCapability_IgnoresTheRequestAndReportsNoSettings()
    {
        var host = (ICockpitHost)Substitute.ForPartsOf<HostWithoutSettingsAccess>();

        var open = () => host.ShowSettingsAsync();

        await open();
        Assert.False(host.HasSettings);
    }

    private static ICockpitHost NewHost(IPluginContributionSink sink, IPluginDialogHost? dialogHost = null) =>
        new CockpitHost(
            "test-plugin",
            "Test Plugin",
            Substitute.For<IServiceProvider>(),
            sink,
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            dialogHost ?? Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance,
            new PluginDiagnostics());

    /// <summary>An older host: implements only what the contract required before a plugin could open its own settings.</summary>
    public abstract class HostWithoutSettingsAccess : ICockpitHost
    {
        public IServiceProvider Services => Substitute.For<IServiceProvider>();

        public ICockpitActions Actions => Substitute.For<ICockpitActions>();

        public IPluginStorage Storage => Substitute.For<IPluginStorage>();

        public void AddSettings(Func<Control> createView)
        {
        }

        public void AddSideMenuButton(string title, Action onInvoke)
        {
        }

        public void AddSideMenuSection(string title, Func<Control> createView)
        {
        }

        public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
            Task.CompletedTask;
    }
}
