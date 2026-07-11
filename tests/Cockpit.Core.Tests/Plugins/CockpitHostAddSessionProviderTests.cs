using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Infrastructure.Claude;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="CockpitHost.AddSessionProvider"/> (#45): a plugin's registration reaches the shared
/// <see cref="IPluginProviderRegistry"/> singleton resolved from the host's service provider — the same
/// registry <c>SessionDriverFactory</c> consults when starting a plugin-backed session.
/// </summary>
public class CockpitHostAddSessionProviderTests
{
    [Fact]
    public void AddSessionProvider_RegistersIntoTheSharedPluginProviderRegistry()
    {
        var registry = new PluginProviderRegistry();
        var services = new ServiceCollection().AddSingleton<IPluginProviderRegistry>(registry).BuildServiceProvider();
        var host = new CockpitHost(
            "gemini-provider",
            services,
            Substitute.For<IPluginContributionSink>(),
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>());
        var registration = new SessionProviderRegistration(
            ProviderId: "gemini-provider.gemini",
            DisplayName: "Gemini",
            CreateDriverFactory: _ => Substitute.For<IPluginSessionDriverFactory>(),
            Capabilities: new PluginSessionCapabilities(true, false, false, false, false),
            CreateConfigView: _ => Substitute.For<IPluginProviderConfigView>());

        host.AddSessionProvider(registration);

        registry.Resolve("gemini-provider.gemini").Should().Be(registration);
    }
}
