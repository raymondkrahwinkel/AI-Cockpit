using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Mcp;

// Persists the shared MCP-server registry under the `mcpServers` section of `cockpit.json`
// (same read-modify-write-the-whole-file pattern as the other section stores, so siblings stay intact).
internal sealed class McpServerStore : IMcpServerStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public McpServerStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path, and at a key holder that is not the process-wide one.
    internal McpServerStore(string configFilePath, ISecretKeyHolder? keyHolder = null)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath, keyHolder);
    }

    public async Task<IReadOnlyList<McpServerConfig>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return _WithDistinctIdentities(configFile?.McpServers?.Select(entry => entry.ToDomain()) ?? []);
    }

    public Task SaveAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.McpServers = servers.Select(McpServerEntry.FromDomain).ToList(),
            cancellationToken);

    // AC-403: guarantees no two rows share an identity — the first claimant keeps it, a later one is pushed to
    // sign-in-again rather than silently sharing a credential across two endpoints on the same host.
    private static List<McpServerConfig> _WithDistinctIdentities(IEnumerable<McpServerConfig> servers)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var distinct = new List<McpServerConfig>();

        foreach (var (server, row) in servers.Select((server, row) => (server, row)))
        {
            var identity = server.IdentityKey;
            if (!taken.Add(identity))
            {
                identity = McpServerIdentity.LegacyIdFor(server.Name);
                if (!taken.Add(identity))
                {
                    identity = McpServerIdentity.UnmatchableIdForRow(row);
                    taken.Add(identity);
                }
            }

            distinct.Add(server with { Id = identity });
        }

        return distinct;
    }
}
