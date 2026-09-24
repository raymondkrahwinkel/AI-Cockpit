using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.Core;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Projects;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Backend.Tests.Mcp;

/// <summary>
/// AC-1375 acceptance 1: the assistant's acting endpoint and <c>cockpit-node</c> mount from a container of Core and
/// Infrastructure, with fakes only for the seams the app still fills and a fake pane in the register. The host
/// activates each endpoint's tools as it mounts, so a missing tool dependency is the "Could not start" branch.
/// </summary>
public class AssistantAgentEndpointMountTests
{
    [Theory]
    [InlineData(AssistantIdentity.ActMcpServerName)]
    [InlineData("cockpit-node")]
    public async Task TheEndpoint_MountsOnCoreAndInfrastructure_WithFakesForTheAppSeamsOnly(string serverName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly);
        services.AddSingleton(Substitute.For<ISessionLauncher>());
        services.AddSingleton(Substitute.For<IProjectEditor>());
        services.AddSingleton(Substitute.For<ISessionWatcher>());
        services.AddSingleton(Substitute.For<IAssistantConversation>());
        services.AddSingleton(Substitute.For<IExternalLinkOpener>());
        await using var provider = services.BuildServiceProvider();

        var sessions = (SessionRegistry)provider.GetRequiredService<ISessionRegistry>();
        var handle = Substitute.For<ISessionHandle>();
        handle.PaneId.Returns("fake-pane");
        sessions.Register(handle);

        // The master switch off, whatever this machine's own state says: a node listener binds a fixed port.
        var nodeSettings = Substitute.For<INodeEndpointSettingsStore>();
        nodeSettings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new NodeEndpointSettings());

        var host = new CockpitMcpEndpointHost(
            [provider.GetServices<CockpitMcpEndpoint>().Single(endpoint => endpoint.ServerName == serverName)],
            provider,
            provider.GetRequiredService<McpAuthKey>(),
            provider.GetRequiredService<SessionMcpKeyring>(),
            nodeSettings,
            provider.GetRequiredService<NodeSelfSignedCertificate>(),
            provider.GetRequiredService<NodeSharedSecret>(),
            provider.GetRequiredService<Cockpit.Core.Sessions.SessionMcpMounts>(),
            provider.GetRequiredService<ILoggerFactory>());

        try
        {
            await host.StartAsync(CancellationToken.None);

            Assert.Contains(serverName, host.GetServers().Select(server => server.Name));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            await host.DisposeAsync();
        }
    }
}
