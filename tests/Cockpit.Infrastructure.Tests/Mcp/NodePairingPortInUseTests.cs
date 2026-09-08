using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Tests.Mcp;

public sealed class NodePairingPortInUseTests
{
    [SkippableFact]
    public async Task WhenThePairingPortIsHeld_ItSaysWhyAndKeepsTheAddressEmpty()
    {
        Skip.If(NodeReachableAddress.Resolve() is null, "No real LAN interface on this machine — the positive control cannot advertise a pairing address.");

        var port = _FreePort();
        await using (var host = _Host(port))
        {
            await host.StartAsync(CancellationToken.None);

            Assert.NotNull(host.Address);
            Assert.Null(host.Error);
            await host.StopAsync(CancellationToken.None);
        }

        var occupier = new TcpListener(IPAddress.Any, port);
        occupier.Start();
        try
        {
            await using var host = _Host(port);
            await host.StartAsync(CancellationToken.None);

            Assert.Null(host.Address);
            Assert.Contains($"port {port} is already in use", host.Error ?? "", StringComparison.Ordinal);
        }
        finally
        {
            occupier.Stop();
        }
    }

    private static int _FreePort()
    {
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static NodePairingHost _Host(int port)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ac-1296-{Guid.NewGuid():N}.json");
        var store = new NodeEndpointSettingsStore(path);
        store.SaveAsync(new NodeEndpointSettings { Enabled = true, SharedSecret = "", Port = port }).GetAwaiter().GetResult();
        var certificate = new NodeSelfSignedCertificate(Path.Combine(Path.GetTempPath(), $"ac-1296-{Guid.NewGuid():N}.pfx"));
        return new NodePairingHost(
            store,
            new NodePairingBroker(store, certificate, new NodeSharedSecret(), []),
            certificate,
            new NodeVisibilityPolicy(store),
            NullLoggerFactory.Instance);
    }
}
