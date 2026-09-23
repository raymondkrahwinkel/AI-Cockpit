using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.Core;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Verify;
using NSubstitute;

namespace Cockpit.Backend.Tests.Mcp;

/// <summary>
/// AC-1374 acceptance 1: the four endpoints these gateways power mount from a container of Core and
/// Infrastructure alone, on a register filled with a fake handle rather than an empty one — proving DI actually
/// resolves the moved gateways' constructor dependencies, not just that an empty graph builds.
/// </summary>
public class SmallGatewayEndpointMountTests
{
    [Fact]
    public async Task TheFourEndpoints_MountOnCoreAndInfrastructureAlone_WithARegisteredFakeHandle()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly);
        var provider = services.BuildServiceProvider();

        var sessions = (SessionRegistry)provider.GetRequiredService<ISessionRegistry>();
        var handle = Substitute.For<ISessionHandle>();
        handle.PaneId.Returns("fake-pane");
        sessions.Register(handle);

        var endpoints = new[]
        {
            new CockpitMcpEndpoint("cockpit-session", typeof(SessionStatusTools), AlwaysMounted: true),
            new CockpitMcpEndpoint("cockpit-verify", typeof(VerifyMcpTools)),
            new CockpitMcpEndpoint("cockpit-agents", typeof(AgentsMcpTools), AlwaysMounted: true),
            new CockpitMcpEndpoint(AssistantIdentity.McpServerName, typeof(AssistantReadMcpTools), Internal: true),
        };

        var host = new CockpitMcpEndpointHost(
            endpoints,
            provider,
            provider.GetRequiredService<McpAuthKey>(),
            provider.GetRequiredService<SessionMcpKeyring>(),
            provider.GetRequiredService<INodeEndpointSettingsStore>(),
            provider.GetRequiredService<NodeSelfSignedCertificate>(),
            provider.GetRequiredService<NodeSharedSecret>(),
            provider.GetRequiredService<Cockpit.Core.Sessions.SessionMcpMounts>(),
            provider.GetRequiredService<ILoggerFactory>());

        try
        {
            await host.StartAsync(CancellationToken.None);

            var mounted = host.GetServers().Select(server => server.Name).ToHashSet(StringComparer.Ordinal);

            Assert.Contains("cockpit-session", mounted);
            Assert.Contains("cockpit-verify", mounted);
            Assert.Contains("cockpit-agents", mounted);
            Assert.Contains(AssistantIdentity.McpServerName, mounted);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            await host.DisposeAsync();
        }
    }
}
