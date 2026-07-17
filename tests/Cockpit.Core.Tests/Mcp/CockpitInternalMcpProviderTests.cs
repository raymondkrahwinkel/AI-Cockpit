using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Infrastructure.Delegation;
using Cockpit.Infrastructure.Mcp;
using FluentAssertions;
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
            loggerFactory: NullLoggerFactory.Instance);

        // Nothing mounted yet: the fan-out sees no cockpit-hosted server.
        host.GetServers().Should().BeEmpty();

        var enabled = true;
        await host.MountAsync("cockpit-probe", new ProbeTools(), isEnabled: () => enabled);

        var mounted = host.GetServers().Should().ContainSingle().Subject;
        mounted.Name.Should().Be("cockpit-probe");
        mounted.CockpitHosted.Should().BeTrue();
        mounted.Url.Should().StartWith("http://127.0.0.1:").And.EndWith("/mcp");
        mounted.Enabled.Should().BeTrue();

        // The gate is read on every call, so flipping the plugin's own setting changes the answer with no rebind.
        enabled = false;
        host.GetServers().Should().ContainSingle().Which.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Orchestrator_LoadsTheToggleAtStartup_ThenFlipsAndPersistsOnSet()
    {
        var store = Substitute.For<IDelegationSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new DelegationSettings { McpEnabled = false });

        var server = new OrchestratorMcpServer(
            Substitute.For<IDelegationService>(),
            new McpAuthKey(),
            store,
            NullLoggerFactory.Instance);

        // Before it has bound a port there is nothing to hand the fan-out.
        server.GetServers().Should().BeEmpty();

        await server.StartAsync(default);
        try
        {
            // Startup honoured the persisted off-state, and the server names itself a cockpit-hosted endpoint.
            var started = server.GetServers().Should().ContainSingle().Subject;
            started.Name.Should().Be(OrchestratorMcpServer.ServerName);
            started.CockpitHosted.Should().BeTrue();
            started.Enabled.Should().BeFalse();

            await server.SetMcpEnabledAsync(true);

            server.GetServers().Should().ContainSingle().Which.Enabled.Should().BeTrue();
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
