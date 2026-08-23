using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;

namespace Cockpit.Infrastructure.Mcp;

// AC-353: bridges the MCP client's token cache to the cockpit's storage for one server — without it,
// ClientOAuthOptions.TokenCache defaults to an in-memory cache that dies with the connection. `serverId` is the
// stable IdentityKey (AC-403); `renewalMargin` (AC-771) is subtracted from the reported lifetime.
internal sealed class McpOAuthTokenCache(
    string serverId,
    string serverName,
    string? resourceUrl,
    IMcpOAuthTokenStore store,
    ILogger logger,
    TimeSpan renewalMargin = default) : ITokenCache
{
    public async ValueTask StoreTokensAsync(TokenContainer token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            // Worth a line: this is the SDK reporting a token exchange that produced nothing usable, and it is
            // otherwise indistinguishable from a renewal that never ran.
            logger.LogWarning("MCP server {Server} returned a token response with no access token; nothing was stored.", serverName);
            return;
        }

        // RFC 6749 §6: a refresh response may omit the refresh token, meaning "keep the one you have" — but only
        // the stored record's own token, since AC-403's rename-survival could otherwise launder one host's grant
        // into a record now pointed at another host.
        var existing = await store.GetAsync(serverId, cancellationToken).ConfigureAwait(false);
        var inheritable = existing is not null && existing.IsForResource(resourceUrl) ? existing.RefreshToken : null;
        var refreshToken = string.IsNullOrWhiteSpace(token.RefreshToken) ? inheritable : token.RefreshToken;

        await store.SaveAsync(
            serverId,
            serverName,
            new McpOAuthToken
            {
                AccessToken = token.AccessToken,
                Scheme = string.IsNullOrWhiteSpace(token.TokenType) ? "Bearer" : token.TokenType,
                RefreshToken = refreshToken,
                ExpiresAt = _ExpiresAt(token),
                Scope = token.Scope,
                ResourceUrl = resourceUrl,
                // AC-505: without these the refresh token is unusable beyond this connection — a fresh connect
                // starts a brand-new provider with no client identity to present, so this restores it.
                ClientId = token.ClientId,
                ClientSecret = token.ClientSecret,
                TokenEndpointAuthMethod = token.TokenEndpointAuthMethod,
                AuthorizationServer = token.AuthorizationServer,
            },
            cancellationToken).ConfigureAwait(false);

        // The expiry and whether a renewal is still possible — never the token, in any form, not even abbreviated
        // (Iron Law #8). Between this line and the coordinator's, a session that lost a server has a trail: when the
        // credential was issued, when it runs out, and whether it can renew itself again.
        logger.LogInformation(
            "Stored a new access token for MCP server {Server}; it expires at {ExpiresAt} and {RefreshState}.",
            serverName,
            _ExpiresAt(token),
            string.IsNullOrWhiteSpace(refreshToken) ? "carries no refresh token, so it cannot renew itself" : "can renew itself");
    }

    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken = default)
    {
        var stored = await store.GetAsync(serverId, cancellationToken).ConfigureAwait(false);

        // The stored token isn't automatically usable — its address can have changed since issuance, so a mismatch
        // reads as having no token at all rather than sending one host's credential to another.
        if (stored is null || !stored.IsForResource(resourceUrl))
        {
            return null;
        }

        // ExpiresIn is relative to ObtainedAt, so it must be rebuilt from the stored absolute instant, not a fresh
        // ObtainedAt. AC-771: minus the caller's margin, since the SDK renews on IsExpired alone with no margin of
        // its own.
        var obtainedAt = DateTimeOffset.UtcNow;
        int? remaining = stored.ExpiresAt is { } expiresAt
            ? (int)Math.Max(0, Math.Round((expiresAt - renewalMargin - obtainedAt).TotalSeconds))
            : null;

        return new TokenContainer
        {
            AccessToken = stored.AccessToken,
            TokenType = stored.Scheme,
            RefreshToken = stored.RefreshToken,
            ExpiresIn = remaining,
            Scope = stored.Scope,
            ObtainedAt = obtainedAt,
            // Restores the client identity the refresh token above was issued under (AC-505) — without this, a
            // fresh provider instance (every connect attempt builds one) has no client ID to present, never
            // attempts the refresh grant at all, and falls straight back to a full interactive sign-in.
            ClientId = stored.ClientId,
            ClientSecret = stored.ClientSecret,
            TokenEndpointAuthMethod = stored.TokenEndpointAuthMethod,
            AuthorizationServer = stored.AuthorizationServer,
        };
    }

    private static DateTimeOffset? _ExpiresAt(TokenContainer token) =>
        token.ExpiresIn is > 0 ? token.ObtainedAt.AddSeconds(token.ExpiresIn.Value) : null;
}
