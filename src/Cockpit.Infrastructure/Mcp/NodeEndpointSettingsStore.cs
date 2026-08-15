using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Mcp;

// Persists the network-node master switch and its shared secret under the `nodeEndpoint` section of
// `cockpit.json` (AC-790), going through `CockpitConfigFileAccess` so it leaves every other section untouched —
// the same pattern as `TerminalAccessSettingsStore`.
internal sealed class NodeEndpointSettingsStore : INodeEndpointSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public NodeEndpointSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal NodeEndpointSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<NodeEndpointSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.NodeEndpoint?.ToDomain() ?? NodeEndpointSettings.Default;
    }

    public Task SaveAsync(NodeEndpointSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.NodeEndpoint = NodeEndpointSettingsEntry.FromDomain(settings),
            cancellationToken);
}
