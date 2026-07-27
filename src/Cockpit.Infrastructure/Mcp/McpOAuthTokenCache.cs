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
internal sealed class McpOAuthTokenCache(string serverName, string? resourceUrl, IMcpOAuthTokenStore store) : ITokenCache
{
    public async ValueTask StoreTokensAsync(TokenContainer token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            return;
        }

        // RFC 6749 §6: a refresh response may leave the refresh token out, which means "keep the one you have". Taking
        // the response at face value would throw it away on the first renewal against any server that does not rotate,
        // and every later expiry would then ask the operator to sign in again for no reason.
        var existing = await store.GetAsync(serverName, cancellationToken).ConfigureAwait(false);
        var refreshToken = string.IsNullOrWhiteSpace(token.RefreshToken) ? existing?.RefreshToken : token.RefreshToken;

        await store.SaveAsync(
            serverName,
            new McpOAuthToken
            {
                AccessToken = token.AccessToken,
                Scheme = string.IsNullOrWhiteSpace(token.TokenType) ? "Bearer" : token.TokenType,
                RefreshToken = refreshToken,
                ExpiresAt = _ExpiresAt(token),
                Scope = token.Scope,
                ResourceUrl = resourceUrl,
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken = default)
    {
        var stored = await store.GetAsync(serverName, cancellationToken).ConfigureAwait(false);

        // A token found by name is not automatically this server's: the name can now belong to a different address
        // (a project's own entry replaces a registry server by name, and a rename does the same). Handing it over
        // would send one host's credential to another, so a mismatch reads as having no token at all.
        if (stored is null || !stored.IsForResource(resourceUrl))
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
