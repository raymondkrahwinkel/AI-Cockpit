using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="CockpitHost.GetMcpServerAuthStateAsync"/>/<see cref="CockpitHost.SignInMcpServerAsync"/> falling back
/// to an <see cref="IPluginMcpProvider"/>'s own project-agnostic <see cref="IPluginMcpProvider.GetMcpServers()"/>
/// when the shared <see cref="IMcpServerStore"/> has no entry under the name (AC-504): a plugin whose servers are
/// delivered to sessions per-project (Depot, one server per connection) no longer pushes them into that registry,
/// so a sign-in — which happens from the plugin's own settings view, with no project of its own to scope by — would
/// otherwise always find nothing there.
/// </summary>
public class CockpitHostPluginOAuthFallbackTests
{
    [Fact]
    public async Task GetMcpServerAuthStateAsync_NameOnlyKnownToAPluginProvider_ResolvesThroughTheFallback()
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>());
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(Arg.Is<McpServerConfig>(config => config.Name == "Depot: Work"), Arg.Any<CancellationToken>())
            .Returns(McpAuthState.AuthorizationRequired);
        var provider = new _FakePluginMcpProvider([new McpServerContribution("Depot: Work", "https://depot.example.com/mcp") { OAuthAuthority = "https://depot.example.com" }]);
        var host = _BuildHost(store, coordinator, provider);

        var state = await host.GetMcpServerAuthStateAsync("Depot: Work");

        Assert.Equal(PluginMcpAuthState.AuthorizationRequired, state);
    }

    [Fact]
    public async Task GetMcpServerAuthStateAsync_RegistryHasTheName_NeverConsultsPluginProviders()
    {
        var registryServer = new McpServerConfig { Name = "Depot: Work", Auth = McpServerAuth.OAuth, Url = "https://depot.example.com/mcp" };
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig> { registryServer });
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(registryServer, Arg.Any<CancellationToken>()).Returns(McpAuthState.Authorized);
        var provider = new _FakePluginMcpProvider([new McpServerContribution("Depot: Work", "https://stale.example.com/mcp")]);
        var host = _BuildHost(store, coordinator, provider);

        var state = await host.GetMcpServerAuthStateAsync("Depot: Work");

        Assert.Equal(PluginMcpAuthState.Authorized, state);
        Assert.False(provider.WasAsked);
    }

    [Fact]
    public async Task SignInMcpServerAsync_NameOnlyKnownToAPluginProvider_DrivesTheInteractiveSignIn()
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>());
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Is<McpServerConfig>(config => config.Name == "Depot: Work"), interactive: true, Arg.Any<CancellationToken>())
            .Returns(McpOAuthAccess.Authorized("token-123"));
        var provider = new _FakePluginMcpProvider([new McpServerContribution("Depot: Work", "https://depot.example.com/mcp") { OAuthAuthority = "https://depot.example.com" }]);
        var host = _BuildHost(store, coordinator, provider);

        var outcome = await host.SignInMcpServerAsync("Depot: Work");

        Assert.Equal(PluginMcpSignInOutcome.Authorized, outcome);
    }

    [Fact]
    public async Task GetMcpServerAuthStateAsync_NoProviderKnowsTheName_AnswersUnknownWithoutThrowing()
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>());
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        var host = _BuildHost(store, coordinator, new _FakePluginMcpProvider([]));

        var state = await host.GetMcpServerAuthStateAsync("Depot: Ghost");

        Assert.Equal(PluginMcpAuthState.Unknown, state);
    }

    // A plugin that throws while listing its servers must not break the lookup for anyone else's sign-in — the
    // same resilience McpServerCatalog applies when assembling a session's tool set.
    [Fact]
    public async Task GetMcpServerAuthStateAsync_APluginProviderThrows_SkipsItAndAnswersUnknown()
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>());
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        var throwingProvider = Substitute.For<IPluginMcpProvider>();
        throwingProvider.GetMcpServers().Returns(_ => throw new InvalidOperationException("boom"));
        var host = _BuildHost(store, coordinator, throwingProvider);

        var state = await host.GetMcpServerAuthStateAsync("Depot: Work");

        Assert.Equal(PluginMcpAuthState.Unknown, state);
    }

    private static CockpitHost _BuildHost(IMcpServerStore store, IMcpOAuthCoordinator coordinator, IPluginMcpProvider provider)
    {
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(coordinator)
            .AddSingleton(provider)
            .BuildServiceProvider();
        return new CockpitHost(
            "depot",
            "Depot",
            services,
            Substitute.For<IPluginContributionSink>(),
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance,
            new PluginDiagnostics());
    }

    private sealed class _FakePluginMcpProvider(IReadOnlyList<McpServerContribution> servers) : IPluginMcpProvider
    {
        public bool WasAsked { get; private set; }

        public IReadOnlyList<McpServerContribution> GetMcpServers()
        {
            WasAsked = true;
            return servers;
        }
    }
}
