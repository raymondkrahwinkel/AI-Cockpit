using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Tests.Mcp;

// AC-1327 reviewbevinding 1: an unpair on this node ends the coupling at once, and presence must say so at once
// too — not wait out its own minute-long expiry window for a controller that is already gone.
public sealed class NodeControllerPresenceTests
{
    [Fact]
    public void UnpairingTheBroker_ClearsPresenceImmediately_WithoutWaitingForExpiry()
    {
        // StubPairing.Pairing is always null, matching what an unpair leaves behind.
        var pairing = new NodeSessionMcpToolsTests.StubPairing();
        var presence = new NodeControllerPresence(TimeProvider.System);
        presence.WatchPairing(pairing);
        presence.Seen("LAPTOP");
        Assert.NotNull(presence.Current);

        pairing.Unpair();

        Assert.Null(presence.Current);
    }
}
