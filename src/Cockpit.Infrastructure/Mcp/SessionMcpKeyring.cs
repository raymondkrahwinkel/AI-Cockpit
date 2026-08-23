using System.Collections.Concurrent;
using System.Security.Cryptography;
using Cockpit.Core.Abstractions;

namespace Cockpit.Infrastructure.Mcp;

// AC-89 (on AC-40): per-session MCP bearer tokens — a spawned session gets its own random `COCKPIT_MCP_KEY`
// instead of the shared app key, attributing a request to the verified session rather than the forgeable
// `session` value an agent declares. Minted at spawn, revoked by whichever component minted it at teardown.
internal sealed class SessionMcpKeyring : ISingletonService
{
    private readonly ConcurrentDictionary<string, string> _tokenToPane = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _paneToToken = new(StringComparer.Ordinal);

    // Mints (or replaces) the token for a session's pane and returns it. The token becomes that session's `COCKPIT_MCP_KEY`.
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

    // The pane a token was minted for, or null if it is not one of ours (e.g. the shared app key, or an unknown value).
    public string? PaneFor(string token) =>
        _tokenToPane.TryGetValue(token, out var paneId) ? paneId : null;

    // Drops a session's token when it ends, keyed by the token the caller minted rather than just the pane — a
    // late revoke by pane alone could take a running session's bearer with it if the pane had since re-minted.
    public void Revoke(string paneId, string token)
    {
        if (_paneToToken.TryGetValue(paneId, out var current) && string.Equals(current, token, StringComparison.Ordinal))
        {
            _paneToToken.TryRemove(paneId, out _);
        }

        _tokenToPane.TryRemove(token, out _);
    }

    // Test seam (AC-143): the live entry count, so a full-lifecycle test can prove both maps are empty
    // after every session closed rather than reasoning about it from the individual `Revoke` calls.
    internal int LiveTokenCount => _tokenToPane.Count;

    // Test seam (AC-143): see `LiveTokenCount`.
    internal int LivePaneCount => _paneToToken.Count;
}
