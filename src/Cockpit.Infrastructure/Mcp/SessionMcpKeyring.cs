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
/// session. It is minted at spawn and kept only in memory. Minting for a pane that already has one replaces it, so a
/// restarted pane never carries a stale identity.
/// </para>
/// <para>
/// Revocation is by the minter: a driver that minted a token drops it when its session ends
/// (<c>PluginSessionDriverAdapter.DisposeAsync</c>), which covers plugin-backed, embedded and delegated sessions.
/// <b>Not yet covered:</b> a TTY session's token (<c>TtyLauncher</c> mints, and the pty's end is handled in the app
/// layer, which cannot reach this class) and the local-model tool loop's (<c>McpToolProvider</c> mints per connect).
/// Those survive until the pane is reused or the app restarts — the same window the shared app key has anyway, so it
/// is a hygiene gap rather than a hole, but it is a gap and this says so rather than claiming otherwise (AC-89).
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

    /// <summary>
    /// Drops a session's token when it ends, so a dead pane's identity cannot be presented again. Takes the token the
    /// caller minted, not just the pane: a session that restarts mints a new token for the same pane before the old
    /// driver is disposed, and revoking by pane alone would drop the live one and leave the running session presenting
    /// a bearer this keyring no longer knows.
    /// </summary>
    public void Revoke(string paneId, string token)
    {
        if (_paneToToken.TryGetValue(paneId, out var current) && string.Equals(current, token, StringComparison.Ordinal))
        {
            _paneToToken.TryRemove(paneId, out _);
        }

        _tokenToPane.TryRemove(token, out _);
    }
}
