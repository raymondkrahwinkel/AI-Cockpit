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

    // Guarantees no two rows come out of here sharing an identity (AC-403 review) — the first to claim one keeps
    // it, and a later claimant is pushed onto the id its own name derives to, or onto one nothing can match.
    //
    // The dialog refuses to save two servers with the same *name*, but there is no such gate on the id and
    // there cannot usefully be one: the id is not shown anywhere, so an operator has no way to see or fix a clash
    // they are being refused over. And a clash is easy to make by hand — copying an `mcpServers` block to add
    // a second endpoint on the same host, changing the name and URL, is exactly the edit that leaves the id behind.
    // Two rows sharing one id share one credential: a sign-out on either withdraws it, a sign-in on either lights
    // up both, and — for two endpoints on one host, which is the very reason to copy a block —
    // `McpOAuthToken.IsForResource` only bounds a token to scheme/host/port, so one row's bearer would
    // be presented at the other's address. That is the defect this ticket exists to remove, reached through the
    // field it introduced.
    //
    // Degrading a duplicate to "sign in again" is the safe side of that trade, so nothing here tries to guess which
    // row the credential belonged to. Deterministic on purpose: two reads of the same file have to agree, or the
    // dialog's post-save resync and the token lookups would key differently for the same row.
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
