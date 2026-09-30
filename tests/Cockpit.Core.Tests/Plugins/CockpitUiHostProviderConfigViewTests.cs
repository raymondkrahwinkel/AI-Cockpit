using Cockpit.App.Plugins;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// AC-1402: a session provider's "add/edit profile" view no longer rides on its registration; the provider's UI part
/// registers it through <see cref="ICockpitUiHost.AddProviderConfigView"/>, for a provider its backend part registered.
/// </summary>
public class CockpitUiHostProviderConfigViewTests
{
    [Fact]
    public void AConfigViewForARegisteredProvider_ReachesTheProfileEditor()
    {
        var (host, views) = _HostWithProvider("gemini-provider.gemini");
        var configView = Substitute.For<IPluginProviderConfigView>();

        host.AddProviderConfigView("gemini-provider.gemini", _ => configView);

        Assert.Same(configView, views.Find("gemini-provider.gemini")?.Invoke(null));
    }

    [Fact]
    public void AConfigViewForAProviderNoPluginRegistered_IsRefusedNamingIt()
    {
        var (host, views) = _HostWithProvider("gemini-provider.gemini");

        var exception = Assert.Throws<InvalidOperationException>(
            () => host.AddProviderConfigView("unknown.provider", _ => Substitute.For<IPluginProviderConfigView>()));

        Assert.Equal((true, null), (exception.Message.Contains("unknown.provider", StringComparison.Ordinal), views.Find("unknown.provider")));
    }

    private static (ICockpitUiHost Host, PluginProviderConfigViews Views) _HostWithProvider(string providerId)
    {
        var registry = new PluginProviderRegistry();
        registry.Register(new SessionProviderRegistration(
            ProviderId: providerId,
            DisplayName: "Gemini",
            CreateDriverFactory: _ => throw new NotSupportedException("Not exercised here."),
            Capabilities: new PluginSessionCapabilities(true, true)));
        var views = new PluginProviderConfigViews();

        var host = TestUiHost.Create(services => services
            .AddSingleton<IPluginProviderRegistry>(registry)
            .AddSingleton<IPluginProviderConfigViews>(views));

        return (host, views);
    }
}
