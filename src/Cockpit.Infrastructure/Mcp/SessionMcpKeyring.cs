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
/// Revocation is by the minter: whichever component minted a token drops it at its own teardown, never a shared
/// cross-component path — a session can flow through more than one of these, so revoking too early would take a
/// live sibling's token with it. <c>PluginSessionDriverAdapter.DisposeAsync</c> covers plugin-backed, embedded and
/// delegated sessions; a TTY session's returned process revokes on its own <c>Dispose</c> (<c>TtyLauncher</c>
/// mints, its <c>TtyProcessOwningSessionFiles</c> wrapper revokes); the local-model tool loop's session revokes on
/// its own <c>DisposeAsync</c> (<c>McpToolProvider.ConnectAsync</c> mints, its <c>McpToolSession</c> revokes).
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
    /// caller minted, not just the pane, because a revoke arriving late cannot tell by pane alone whether the token it
    /// means is still the live one — and dropping by pane would then take a running session's bearer with it.
    /// </summary>
    /// <remarks>
    /// No current path mints twice for one pane with both drivers alive (a pane id is fixed per session view model and
    /// a second start is refused while one is running), so this is a guard against a shape the code could grow rather
    /// than a fix for one it has. It is worth the parameter anyway: the failure it prevents — a live session holding a
    /// bearer the keyring has forgotten — is silent, and the cost of preventing it is one comparison.
    /// </remarks>
    public void Revoke(string paneId, string token)
    {
        if (_paneToToken.TryGetValue(paneId, out var current) && string.Equals(current, token, StringComparison.Ordinal))
        {
            _paneToToken.TryRemove(paneId, out _);
        }

        _tokenToPane.TryRemove(token, out _);
    }

    /// <summary>Test seam (AC-143): the live entry count, so a full-lifecycle test can prove both maps are empty
    /// after every session closed rather than reasoning about it from the individual <see cref="Revoke"/> calls.</summary>
    internal int LiveTokenCount => _tokenToPane.Count;

    /// <summary>Test seam (AC-143): see <see cref="LiveTokenCount"/>.</summary>
    internal int LivePaneCount => _paneToToken.Count;
}
