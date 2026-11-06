using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests;

public abstract class SessionMcpTestFixture : IAsyncLifetime
{
    private protected McpAuthKey AuthKey { get; } = new();
    private protected SessionMcpKeyring Keyring { get; } = new();
    private protected SessionMcpMounts Mounts { get; } = new();
    private protected IMcpServerCatalog EmptyCatalog { get; } = Substitute.For<IMcpServerCatalog>();
    private protected SessionMcpGatewayHost Gateway { get; }

    protected SessionMcpTestFixture()
    {
        EmptyCatalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([]);
        Gateway = new SessionMcpGatewayHost(AuthKey, Keyring, NullLoggerFactory.Instance, mounts: Mounts);
    }

    public async Task InitializeAsync()
    {
        await Gateway.StartAsync(CancellationToken.None);
        Assert.NotNull(Gateway.BoundUrl);
    }

    public async Task DisposeAsync() => await Gateway.DisposeAsync();
}
