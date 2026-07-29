using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Infrastructure.Delegation;
using Cockpit.Infrastructure.Mcp;
using NSubstitute;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// The two cockpit-hosted MCP sources answer the session fan-out live as <see cref="ICockpitInternalMcpProvider"/>s
/// (AC-40): they must project their live loopback URL, mark themselves <c>CockpitHosted</c>, and report the enabled
/// state their toggle currently gives — read each time, so a flip takes effect without a rebind. A real Kestrel
/// loopback endpoint is stood up (the same pattern as <see cref="InProcessMcpHttpServer"/>), since that is the only
/// place the bound URL comes from.
/// </summary>
public class CockpitInternalMcpProviderTests
{
    private sealed class ProbeTools
    {
        [McpServerTool, Description("A probe tool, so the endpoint mounts a non-empty tool set.")]
        public static string Ping() => "pong";
    }

    [Fact]
    public async Task EndpointHost_ReflectsTheLiveIsEnabledGate_AndMarksItselfCockpitHosted()
    {
        await using var host = new CockpitMcpEndpointHost(
            endpoints: [],
            services: new ServiceCollection().BuildServiceProvider(),
            authKey: new McpAuthKey(),
            keyring: new SessionMcpKeyring(),
            loggerFactory: NullLoggerFactory.Instance);

        // Nothing mounted yet: the fan-out sees no cockpit-hosted server.
        Assert.Empty(host.GetServers());

        var enabled = true;
        await host.MountAsync("cockpit-probe", new ProbeTools(), isEnabled: () => enabled);

        var mounted = Assert.Single(host.GetServers());
        Assert.Equal("cockpit-probe", mounted.Name);
        Assert.True(mounted.CockpitHosted);
        Assert.StartsWith("http://127.0.0.1:", mounted.Url);
        Assert.EndsWith("/mcp", mounted.Url);
        Assert.True(mounted.Enabled);

        // The gate is read on every call, so flipping the plugin's own setting changes the answer with no rebind.
        enabled = false;
        Assert.False(Assert.Single(host.GetServers()).Enabled);
    }

    [Fact]
    public async Task EndpointHost_ProjectsTheInternalFlag_SoTheFilterCanHideSpawnScopedEndpoints()
    {
        await using var host = new CockpitMcpEndpointHost(
            endpoints: [],
            services: new ServiceCollection().BuildServiceProvider(),
            authKey: new McpAuthKey(),
            keyring: new SessionMcpKeyring(),
            loggerFactory: NullLoggerFactory.Instance);

        // An ordinary endpoint is not internal; an internal-only one (AC-204, e.g. the Autopilot CEO/step tools)
        // carries the flag through to the fan-out's McpServerConfig so the user-facing selection can hide it.
        await host.MountAsync("cockpit-public", new ProbeTools(), isEnabled: () => true);
        await host.MountAsync("cockpit-private", new ProbeTools(), isEnabled: () => true, isInternal: true);

        var servers = host.GetServers();
        Assert.False(servers.Single(server => server.Name == "cockpit-public").Internal);
        Assert.True(servers.Single(server => server.Name == "cockpit-private").Internal);
    }

    [Fact]
    public async Task Orchestrator_LoadsTheToggleAtStartup_ThenFlipsAndPersistsOnSet()
    {
        var store = Substitute.For<IDelegationSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new DelegationSettings { McpEnabled = false });

        var server = new OrchestratorMcpServer(
            Substitute.For<IDelegationService>(),
            new McpAuthKey(),
            new SessionMcpKeyring(),
            store,
            NullLoggerFactory.Instance);

        // Before it has bound a port there is nothing to hand the fan-out.
        Assert.Empty(server.GetServers());

        await server.StartAsync(default);
        try
        {
            // Startup honoured the persisted off-state, and the server names itself a cockpit-hosted endpoint.
            var started = Assert.Single(server.GetServers());
            Assert.Equal(OrchestratorMcpServer.ServerName, started.Name);
            Assert.True(started.CockpitHosted);
            Assert.False(started.Enabled);

            await server.SetMcpEnabledAsync(true);

            Assert.True(Assert.Single(server.GetServers()).Enabled);
            await store.Received().SaveAsync(
                Arg.Is<DelegationSettings>(settings => settings.McpEnabled), Arg.Any<CancellationToken>());
        }
        finally
        {
            await server.StopAsync(default);
            await server.DisposeAsync();
        }
    }
}
