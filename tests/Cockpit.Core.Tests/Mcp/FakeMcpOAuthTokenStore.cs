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

    public Task<McpOAuthToken?> GetAsync(string serverName, CancellationToken cancellationToken = default)
    {
        Reads++;
        return Task.FromResult(_tokens.TryGetValue(serverName, out var token) ? token : null);
    }

    public Task SaveAsync(string serverName, McpOAuthToken token, CancellationToken cancellationToken = default)
    {
        _tokens[serverName] = token;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string serverName, CancellationToken cancellationToken = default)
    {
        _tokens.Remove(serverName);
        OnRemoved?.Invoke();
        return Task.CompletedTask;
    }
}
