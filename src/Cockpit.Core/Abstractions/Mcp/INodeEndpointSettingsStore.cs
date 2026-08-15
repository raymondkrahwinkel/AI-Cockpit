using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>Persists the network-node master switch and its shared secret (AC-790).</summary>
public interface INodeEndpointSettingsStore
{
    Task<NodeEndpointSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(NodeEndpointSettings settings, CancellationToken cancellationToken = default);
}
