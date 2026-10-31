using Cockpit.Core.Abstractions;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Swarm;

internal sealed class SwarmMigration(SwarmJoinClient client, SwarmJoinHost local) : ISingletonService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _finished = new(StringComparer.Ordinal);

    public async Task MigrateAsync(string address, string credential, string pin, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_finished.Contains(pin))
            {
                return;
            }
            try
            {
                var handshake = await client._BeginAsync(address, credential, pin, ct);
                await client.CompleteAsync(handshake, ct);
                _finished.Add(pin);
                await local.NotifyAsync(handshake.Offer.NodeName, ct);
            }
            catch (NodePairingException ex) when (ex.Error is Core.Swarm.SwarmJoinError.IsAServer or NodePairingError.AlreadyPaired)
            {
                _finished.Add(pin);
            }
        }
        finally { _gate.Release(); }
    }

    public Task OnConnectedAsync(McpServerConfig server, CancellationToken ct)
    {
        if (server.ApiKey is not { Length: > 0 } credential || server.PinnedCertificateFingerprint is not { Length: > 0 } pin
            || !Uri.TryCreate(server.Url, UriKind.Absolute, out var uri) || uri.Port <= NodeEndpointSettings.McpPortOffset)
            return Task.CompletedTask;
        var address = new UriBuilder(uri) { Port = uri.Port - NodeEndpointSettings.McpPortOffset, Path = "/" }.Uri.ToString();
        return MigrateAsync(address, credential, pin, ct);
    }
}
