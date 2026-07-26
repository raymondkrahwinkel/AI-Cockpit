using Cockpit.Infrastructure.Mcp;
using FluentAssertions;

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

        a.Should().NotBe(b);
        keyring.PaneFor(a).Should().Be("pane-a");
        keyring.PaneFor(b).Should().Be("pane-b");
        keyring.PaneFor("not-a-token").Should().BeNull();
    }

    [Fact]
    public void TokenFor_MintingAgainForAPane_ReplacesTheOldTokenSoAStaleOneNoLongerResolves()
    {
        var keyring = new SessionMcpKeyring();

        var first = keyring.TokenFor("pane-a");
        var second = keyring.TokenFor("pane-a");

        second.Should().NotBe(first);
        keyring.PaneFor(first).Should().BeNull("a restarted pane's old token must not still name it");
        keyring.PaneFor(second).Should().Be("pane-a");
    }

    [Fact]
    public void Revoke_DropsThePanesToken()
    {
        var keyring = new SessionMcpKeyring();
        var token = keyring.TokenFor("pane-a");

        keyring.Revoke("pane-a", token);

        keyring.PaneFor(token).Should().BeNull();
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

        keyring.PaneFor(live).Should().Be("pane-a", "the restarted session is still using this one");
        keyring.PaneFor(old).Should().BeNull();
    }
}
