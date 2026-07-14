using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// Persists the shared MCP-server registry under the <c>mcpServers</c> section of <c>cockpit.json</c>
/// (same read-modify-write-the-whole-file pattern as the other section stores, so siblings stay intact).
/// </summary>
internal sealed class McpServerStore : IMcpServerStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public McpServerStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path, and at a key holder that is not the process-wide one.</summary>
    internal McpServerStore(string configFilePath, ISecretKeyHolder? keyHolder = null)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath, keyHolder);
    }

    public async Task<IReadOnlyList<McpServerConfig>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.McpServers?.Select(entry => entry.ToDomain()).ToList() ?? [];
    }

    public Task SaveAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.McpServers = servers.Select(McpServerEntry.FromDomain).ToList(),
            cancellationToken);
}
