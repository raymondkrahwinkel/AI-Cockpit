using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;

namespace Cockpit.Infrastructure.Mcp;

// Bridges the MCP client's token cache to the cockpit's own storage for one server (AC-353).
//
// This is the whole reason the cockpit ever sees a token. Left unset, `ClientOAuthOptions.TokenCache` defaults
// to an in-memory cache owned by the transport: the sign-in works, and the result dies with the connection — so
// every session pays for its own browser login and nothing can be handed to an agent. Pointed here instead, the
// SDK reads the stored token on each request and writes back every renewal, which is what makes one sign-in serve
// every route and survive a restart.
//
// `serverId`: The server's stable `McpServerConfig.IdentityKey` (AC-403) — the key the store files under.
// `serverName`: The server's current name, written alongside the token purely as a label.
// `resourceUrl`: The address the token is being obtained for, so a record cannot be used at another one.
// `store`: Where the token lands.
// `logger`: Where a renewal leaves its trace (AC-524) — this class used to write nothing at all, which
// made an expiry an anecdote instead of an event anyone could go and look up.
// `renewalMargin`: How much life the caller needs the token to have left, subtracted from what this reports (AC-771).
// Zero for a connect that only wants to use whatever is stored.
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

        // RFC 6749 §6: a refresh response may leave the refresh token out, which means "keep the one you have". Taking
        // the response at face value would throw it away on the first renewal against any server that does not rotate,
        // and every later expiry would then ask the operator to sign in again for no reason.
        // The one it keeps has to be its own, though: the stored record is this server's across a rename (AC-403),
        // but the operator can still have pointed that same server at a different host since, and carrying its
        // refresh token over would launder one host's grant into another host's record — the same leak the origin
        // check exists to stop, one layer down.
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
                // Without these, the refresh token above is unusable beyond this one connection: the SDK only
                // attempts a refresh grant once it has a client identity to present, and a fresh connect attempt
                // (a new session, a renewal, a restart) starts a brand-new provider with none — it has to be
                // restored from here (AC-505).
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

        // This server's own token is still not automatically usable here: the address under it can have changed
        // since it was issued (a project's own entry replaces a registry server by name and may carry a different
        // one, and an operator can edit the URL). Handing it over would send one host's credential to another, so a
        // mismatch reads as having no token at all.
        if (stored is null || !stored.IsForResource(resourceUrl))
        {
            return null;
        }

        // ExpiresIn is relative to ObtainedAt, so the pair has to be rebuilt from the absolute instant we stored:
        // handing back the original ExpiresIn with a fresh ObtainedAt would present an expired token as brand new.
        //
        // Less the caller's margin, and that subtraction is the whole of AC-771. The SDK renews on one condition and
        // one only: `TokenContainer.IsExpired`, which is `UtcNow >= ObtainedAt + ExpiresIn` — dead on the second, no
        // margin of its own. The coordinator meanwhile decides a token needs renewing while it still has minutes to
        // live. Between those two lines sat a window per token lifetime in which the coordinator asked for a renewal,
        // the SDK looked at a token it considered perfectly good, refreshed nothing, and the coordinator read the
        // unchanged store as a renewal that had failed — so an ordinary call came back to the agent as an
        // authentication error, twice in a row, and worked again once the token was properly dead. Reporting the
        // margin as already spent is what makes the two agree: the caller asks for a renewal exactly when the SDK
        // will perform one.
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
