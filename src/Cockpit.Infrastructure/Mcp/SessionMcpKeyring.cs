using System.Collections.Concurrent;
using System.Security.Cryptography;
using Cockpit.Core.Abstractions;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// Per-session MCP bearer tokens (AC-89, on AC-40). A spawned session gets its own random token as its
/// <c>COCKPIT_MCP_KEY</c> instead of the shared app key, so a request that reaches a cockpit endpoint can be
/// attributed to the exact session that made it — the transport-verified identity the consent broker scopes remember
/// decisions on, rather than the <c>session</c> value the agent declares (which it can forge to ride another pane's
/// approvals).
/// <para>
/// A token is a capability like the app key: it grants access to the loopback endpoints and additionally names the
/// session. It is minted at spawn, kept only in memory, and revoked when the session ends. Minting for a pane that
/// already has one replaces it, so a restarted pane never carries a stale identity.
/// </para>
/// </summary>
internal sealed class SessionMcpKeyring : ISingletonService
{
    private readonly ConcurrentDictionary<string, string> _tokenToPane = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _paneToToken = new(StringComparer.Ordinal);

    /// <summary>Mints (or replaces) the token for a session's pane and returns it. The token becomes that session's <c>COCKPIT_MCP_KEY</c>.</summary>
    public string TokenFor(string paneId)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (_paneToToken.TryGetValue(paneId, out var previous))
        {
            _tokenToPane.TryRemove(previous, out _);
        }

        _paneToToken[paneId] = token;
        _tokenToPane[token] = paneId;
        return token;
    }

    /// <summary>The pane a token was minted for, or null if it is not one of ours (e.g. the shared app key, or an unknown value).</summary>
    public string? PaneFor(string token) =>
        _tokenToPane.TryGetValue(token, out var paneId) ? paneId : null;

    /// <summary>Drops a session's token when it ends, so a dead pane's identity cannot be presented again.</summary>
    public void Revoke(string paneId)
    {
        if (_paneToToken.TryRemove(paneId, out var token))
        {
            _tokenToPane.TryRemove(token, out _);
        }
    }
}
