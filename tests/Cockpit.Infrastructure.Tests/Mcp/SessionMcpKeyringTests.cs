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

        keyring.Revoke("pane-a");

        keyring.PaneFor(token).Should().BeNull();
    }
}
