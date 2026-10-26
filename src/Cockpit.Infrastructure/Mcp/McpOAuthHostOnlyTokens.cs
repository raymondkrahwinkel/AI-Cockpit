using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// AC-1586: a token for one scope of a server's grant that only the cockpit itself spends, through
// ICockpitHost.CreateAuthorizedHandler(name, scope). Kept in memory and never in the token store, which is what a
// session is handed from; the rotated refresh token it costs does go there, or the next refresh replays a spent one.
internal sealed class McpOAuthHostOnlyTokens(
    IMcpOAuthTokenStore tokenStore,
    McpOAuthScopeNarrower narrower,
    McpOAuthRefreshGate refreshGate) : ISingletonService
{
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(2);

    private readonly Dictionary<(string ServerId, string Scope), _Held> _held = [];
    private readonly Lock _heldLock = new();

    public async Task<McpOAuthAccess> AcquireAsync(McpServerConfig server, string scope, string? rejected, CancellationToken cancellationToken)
    {
        // Only a scope the plugin declared host-only, one at a time: anything else is a token nobody meant to exist.
        if (server.Auth != McpServerAuth.OAuth || !McpOAuthScopeNarrower.Split(server.OAuthHostOnlyScopes).Contains(scope, StringComparer.Ordinal))
        {
            return McpOAuthAccess.AuthorizationRequired;
        }

        if (_Usable(server, scope, rejected, await tokenStore.GetAsync(server.IdentityKey, cancellationToken).ConfigureAwait(false)) is { } held)
        {
            return McpOAuthAccess.Authorized(held);
        }

        return await refreshGate.RunAsync(server.IdentityKey, () => _RefreshAsync(server, scope, rejected, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private async Task<McpOAuthAccess> _RefreshAsync(McpServerConfig server, string scope, string? rejected, CancellationToken cancellationToken)
    {
        // Read again inside the gate: whoever held it may have refreshed already, and either way rotated the grant.
        var stored = await tokenStore.GetAsync(server.IdentityKey, cancellationToken).ConfigureAwait(false);
        if (_Usable(server, scope, rejected, stored) is { } held)
        {
            return McpOAuthAccess.Authorized(held);
        }

        if (stored is null || !stored.IsForResource(server.Url)
            || await narrower.NarrowAsync(server, stored, scope, cancellationToken).ConfigureAwait(false) is not { } narrowed)
        {
            return McpOAuthAccess.AuthorizationRequired;
        }

        // CancellationToken.None: the server has rotated the grant, so losing the new refresh token now would revoke it.
        // Saved only over the record it came from; a sign-in that moved on meanwhile keeps its own, and this token is dropped.
        var current = await tokenStore.GetAsync(server.IdentityKey, CancellationToken.None).ConfigureAwait(false);
        if (current is null || !string.Equals(current.RefreshToken, stored.RefreshToken, StringComparison.Ordinal))
        {
            return McpOAuthAccess.AuthorizationRequired;
        }

        await tokenStore.SaveAsync(server.IdentityKey, server.Name, current with { RefreshToken = narrowed.RefreshToken }, CancellationToken.None).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(narrowed.AccessToken))
        {
            return McpOAuthAccess.AuthorizationRequired;
        }

        lock (_heldLock)
        {
            _held[(server.IdentityKey, scope)] = new _Held(narrowed.AccessToken, narrowed.ExpiresAt, narrowed.RefreshToken);
        }

        return McpOAuthAccess.Authorized(narrowed.AccessToken);
    }

    // Held only while the stored grant is the one it came from: a sign-out, a new sign-in or a renewal elsewhere changes
    // the stored refresh token, and a token from another sign-in must not outlive it.
    private string? _Usable(McpServerConfig server, string scope, string? rejected, McpOAuthToken? stored)
    {
        lock (_heldLock)
        {
            if (!_held.TryGetValue((server.IdentityKey, scope), out var held))
            {
                return null;
            }

            var current = stored is not null && stored.IsForResource(server.Url)
                && string.Equals(stored.RefreshToken, held.RefreshToken, StringComparison.Ordinal)
                && !string.Equals(held.AccessToken, rejected, StringComparison.Ordinal)
                && (held.ExpiresAt is null || held.ExpiresAt.Value - ExpiryMargin > DateTimeOffset.UtcNow);
            if (!current)
            {
                _held.Remove((server.IdentityKey, scope));
                return null;
            }

            return held.AccessToken;
        }
    }

    private sealed record _Held(string AccessToken, DateTimeOffset? ExpiresAt, string? RefreshToken);
}
