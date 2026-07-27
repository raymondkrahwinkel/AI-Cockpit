using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// Persists MCP OAuth tokens under the <c>mcpOAuthTokens</c> section of <c>cockpit.json</c> (same
/// read-modify-write-the-whole-file pattern as the other section stores, so siblings stay intact).
/// </summary>
internal sealed class McpOAuthTokenStore : IMcpOAuthTokenStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public McpOAuthTokenStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path, and at a key holder that is not the process-wide one.</summary>
    internal McpOAuthTokenStore(string configFilePath, ISecretKeyHolder? keyHolder = null)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath, keyHolder);
    }

    public async Task<McpOAuthToken?> GetAsync(string serverName, CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.McpOAuthTokens?.FirstOrDefault(entry => _Matches(entry, serverName))?.ToDomain();
    }

    public Task SaveAsync(string serverName, McpOAuthToken token, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file =>
            {
                file.McpOAuthTokens.RemoveAll(entry => _Matches(entry, serverName));
                file.McpOAuthTokens.Add(McpOAuthTokenEntry.FromDomain(serverName, token));
            },
            cancellationToken);

    public Task RemoveAsync(string serverName, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.McpOAuthTokens.RemoveAll(entry => _Matches(entry, serverName)),
            cancellationToken);

    // Server names are matched the way the registry matches them everywhere else — case-insensitively — so that
    // renaming the casing of a server in the dialog does not orphan its token behind a name nothing looks up again.
    private static bool _Matches(McpOAuthTokenEntry entry, string serverName) =>
        string.Equals(entry.ServerName, serverName, StringComparison.OrdinalIgnoreCase);
}
