using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>The per-session MCP token ledger (AC-89): a token names its pane, a fresh mint replaces the old, and a revoke drops it.</summary>
public class SessionMcpKeyringTests
{
    [Fact]
    public void TokenFor_MintsADistinctTokenThatResolvesBackToItsPane()
    {
        var keyring = new SessionMcpKeyring();

        var a = keyring.TokenFor("pane-a");
        var b = keyring.TokenFor("pane-b");

        Assert.NotEqual(b, a);
        Assert.Equal("pane-a", keyring.PaneFor(a));
        Assert.Equal("pane-b", keyring.PaneFor(b));
        Assert.Null(keyring.PaneFor("not-a-token"));
    }

    [Fact]
    public void TokenFor_MintingAgainForAPane_ReplacesTheOldTokenSoAStaleOneNoLongerResolves()
    {
        var keyring = new SessionMcpKeyring();

        var first = keyring.TokenFor("pane-a");
        var second = keyring.TokenFor("pane-a");

        Assert.NotEqual(first, second);
        Assert.Null(keyring.PaneFor(first));
        Assert.Equal("pane-a", keyring.PaneFor(second));
    }

    [Fact]
    public void Revoke_DropsThePanesToken()
    {
        var keyring = new SessionMcpKeyring();
        var token = keyring.TokenFor("pane-a");

        keyring.Revoke("pane-a", token);

        Assert.Null(keyring.PaneFor(token));
    }

    /// <summary>
    /// The restart race: a pane that restarts mints its replacement token before the old driver is disposed, so the
    /// revoke that follows arrives after the new token is already live. Revoking by pane alone would drop it and leave
    /// the running session holding a bearer this keyring no longer recognises.
    /// </summary>
    [Fact]
    public void Revoke_WithASupersededToken_LeavesTheLiveOneAlone()
    {
        var keyring = new SessionMcpKeyring();
        var old = keyring.TokenFor("pane-a");
        var live = keyring.TokenFor("pane-a");

        keyring.Revoke("pane-a", old);

        // Only the live token is asserted here: the superseded one stopped resolving at the second mint, not at this
        // revoke, and TokenFor_MintingAgainForAPane_ReplacesTheOldToken… is what pins that. Asserting it again would
        // read as coverage this test does not provide.
        Assert.Equal("pane-a", keyring.PaneFor(live));
    }
}
