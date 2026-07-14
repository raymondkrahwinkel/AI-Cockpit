using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="CockpitHost.ShowToast"/> (#74): a plugin's toast reaches the app's own in-app toast surface
/// (<see cref="IToastService"/>, #61), action button and all. The severity mapping is asserted per member
/// because the two enums are declared independently (plugin isolation) and are mapped by name, not ordinal —
/// a silent mis-map would show the wrong colour and timeout.
/// </summary>
public class CockpitHostShowToastTests
{
    [Fact]
    public void ShowToast_WithAnAction_PassesTheMessageAndActionToTheToastService()
    {
        var toastService = Substitute.For<IToastService>();
        var host = _BuildHost(toastService);
        var onAction = () => { };

        host.ShowToast("Review requested — #7 Fix the pump (acme/api)", PluginToastSeverity.Information, "Open in browser", onAction);

        toastService.Received(1).Show(
            "Review requested — #7 Fix the pump (acme/api)",
            ToastSeverity.Information,
            "Open in browser",
            onAction);
    }

    [Theory]
    [InlineData(PluginToastSeverity.Success, ToastSeverity.Success)]
    [InlineData(PluginToastSeverity.Warning, ToastSeverity.Warning)]
    [InlineData(PluginToastSeverity.Information, ToastSeverity.Information)]
    [InlineData(PluginToastSeverity.Error, ToastSeverity.Error)]
    public void ShowToast_MapsEachSeverityToItsHostCounterpart(PluginToastSeverity plugin, ToastSeverity expected)
    {
        var toastService = Substitute.For<IToastService>();
        var host = _BuildHost(toastService);

        host.ShowToast("Something happened", plugin);

        toastService.Received(1).Show("Something happened", expected, null, null);
    }

    // Typed as the contract a plugin actually holds, so the call goes through ICockpitHost's defaulted
    // parameters — the same way a plugin invokes it.
    private static ICockpitHost _BuildHost(IToastService toastService)
    {
        var services = new ServiceCollection().AddSingleton(toastService).BuildServiceProvider();
        return new CockpitHost(
            "github-pull-requests",
            "GitHub Pull Requests",
            services,
            Substitute.For<IPluginContributionSink>(),
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance);
    }
}
