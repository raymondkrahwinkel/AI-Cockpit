using Cockpit.Plugin.LocalCi.Sessions;

namespace Cockpit.Plugin.LocalCi.Tests;

public class SessionCheckoutsTests
{
    [Fact]
    public void RemembersACheckoutByPane()
    {
        var checkouts = new SessionCheckouts();
        checkouts.Remember(new FakeSession("pane-1", "/repo/one"));

        Assert.Equal("/repo/one", checkouts.CheckoutFor("pane-1"));
    }

    [Fact]
    public void ForgetDropsTheCheckout()
    {
        // The leak this guards: without Forget, a closed pane's context stayed in the map for the life of the app,
        // pinning the whole session. After the SessionClosed hook forgets it, the pane is unknown again.
        var checkouts = new SessionCheckouts();
        checkouts.Remember(new FakeSession("pane-1", "/repo/one"));

        checkouts.Forget("pane-1");

        Assert.Null(checkouts.CheckoutFor("pane-1"));
    }

    [Fact]
    public void ForgettingAnUnknownPaneIsANoOp()
    {
        var checkouts = new SessionCheckouts();

        checkouts.Forget("never-seen");

        Assert.Null(checkouts.CheckoutFor("never-seen"));
    }
}
