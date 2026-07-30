using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// An in-memory <see cref="IMcpOAuthTokenStore"/> that counts its reads, so a test can tell the cheap path
/// ("nothing stored, nobody to ask") from the one that goes out and tries to renew: the first reads once, the
/// second reads again after the attempt.
/// </summary>
internal sealed class FakeMcpOAuthTokenStore : IMcpOAuthTokenStore
{
    private readonly Dictionary<string, McpOAuthToken> _tokens = new(StringComparer.OrdinalIgnoreCase);

    public int Reads { get; private set; }

    /// <summary>Runs right after a removal, so a test can stand in for what an authorization flow would write next.</summary>
    public Action? OnRemoved { get; set; }

    public Task<McpOAuthToken?> GetAsync(string serverId, CancellationToken cancellationToken = default)
    {
        Reads++;
        return Task.FromResult(_tokens.TryGetValue(serverId, out var token) ? token : null);
    }

    public Task SaveAsync(string serverId, string serverName, McpOAuthToken token, CancellationToken cancellationToken = default)
    {
        _tokens[serverId] = token;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string serverId, CancellationToken cancellationToken = default)
    {
        _tokens.Remove(serverId);
        OnRemoved?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>Nothing to migrate in a store that never had a name-keyed era — the real one's own tests cover it.</summary>
    public Task AdoptLegacyEntriesAsync(IReadOnlyDictionary<string, string> idsByServerName, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
