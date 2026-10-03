using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions.Mcp;

namespace Cockpit.Server;

// AC-1356: connect keys are the server's only way in, so the node door is on whatever cockpit.json says, and a start
// whose door does not listen is a failed start, not a "running" server nobody can reach.
internal static class NodeDoor
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    // A secret is minted when none exists: an empty one must never match.
    public static async Task EnableAsync(IServiceProvider services)
    {
        var store = services.GetRequiredService<INodeEndpointSettingsStore>();
        var settings = await store.LoadAsync();
        if (settings.Enabled && !string.IsNullOrEmpty(settings.SharedSecret))
        {
            return;
        }

        settings = settings with { Enabled = true, SharedSecret = string.IsNullOrEmpty(settings.SharedSecret) ? Guid.NewGuid().ToString("N") : settings.SharedSecret };
        await store.SaveAsync(settings);
    }

    // Null when something accepts a connection on the door's port, else why not.
    public static async Task<string?> ProbeAsync(IServiceProvider services)
    {
        // A held port is reported by the endpoint host; a TCP probe would reach whoever holds it and call that listening.
        var reported = services.GetServices<ICockpitInternalMcpProvider>().Select(provider => provider.NodeListenerError).FirstOrDefault(error => error is not null);
        if (reported is not null)
        {
            return reported;
        }

        // The live address, so a port the OS picked (0) is probed as well as a fixed one.
        var address = services.GetServices<ICockpitInternalMcpProvider>().SelectMany(provider => provider.GetNodeAddresses()).FirstOrDefault();
        if (address is null)
        {
            return "the node endpoint has no listening address.";
        }

        var port = new Uri(address.Url).Port;

        // The endpoint mounts during Start; retried a moment so a slow mount is not read as a refusal.
        using var timeout = new CancellationTokenSource(ProbeTimeout);
        try
        {
            while (true)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                    return null;
                }
                catch (SocketException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return $"nothing accepts a connection on port {port}.";
        }
    }
}
