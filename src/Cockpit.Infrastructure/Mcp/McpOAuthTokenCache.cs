using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using ModelContextProtocol.Authentication;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// Bridges the MCP client's token cache to the cockpit's own storage for one server (AC-353).
/// <para>
/// This is the whole reason the cockpit ever sees a token. Left unset, <c>ClientOAuthOptions.TokenCache</c> defaults
/// to an in-memory cache owned by the transport: the sign-in works, and the result dies with the connection — so
/// every session pays for its own browser login and nothing can be handed to an agent. Pointed here instead, the
/// SDK reads the stored token on each request and writes back every renewal, which is what makes one sign-in serve
/// every route and survive a restart.
/// </para>
/// </summary>
internal sealed class McpOAuthTokenCache(string serverName, IMcpOAuthTokenStore store) : ITokenCache
{
    public async ValueTask StoreTokensAsync(TokenContainer token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            return;
        }

        await store.SaveAsync(
            serverName,
            new McpOAuthToken
            {
                AccessToken = token.AccessToken,
                Scheme = string.IsNullOrWhiteSpace(token.TokenType) ? "Bearer" : token.TokenType,
                RefreshToken = token.RefreshToken,
                ExpiresAt = _ExpiresAt(token),
                Scope = token.Scope,
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken = default)
    {
        var stored = await store.GetAsync(serverName, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        // ExpiresIn is relative to ObtainedAt, so the pair has to be rebuilt from the absolute instant we stored:
        // handing back the original ExpiresIn with a fresh ObtainedAt would present an expired token as brand new.
        var obtainedAt = DateTimeOffset.UtcNow;
        int? remaining = stored.ExpiresAt is { } expiresAt
            ? (int)Math.Max(0, Math.Round((expiresAt - obtainedAt).TotalSeconds))
            : null;

        return new TokenContainer
        {
            AccessToken = stored.AccessToken,
            TokenType = stored.Scheme,
            RefreshToken = stored.RefreshToken,
            ExpiresIn = remaining,
            Scope = stored.Scope,
            ObtainedAt = obtainedAt,
        };
    }

    private static DateTimeOffset? _ExpiresAt(TokenContainer token) =>
        token.ExpiresIn is > 0 ? token.ObtainedAt.AddSeconds(token.ExpiresIn.Value) : null;
}
