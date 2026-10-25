using Cockpit.Plugin.Browser.UI.Share;

namespace Cockpit.Plugin.Browser.Tests;

// AC-1728: when an agent may act in one of Raymond's tabs. Every control tool goes through this one check.
public class TabControlTests
{
    // L2b (authorisation), criteria 1 to 4: watching is not enough, control is exactly one session on one tab, and
    // Raymond taking over, Stop, closing the tab, the session ending or a move to another site refuse the next action.
    [Fact]
    public void Control_IsOneSessionOnOneTab_AndNeverFollowsFromWatching()
    {
        var grants = new TabGrantRegistry();
        grants.Share("pane-a", "A", "tab-1", "https://github.com", TabShareService.Watch);
        Assert.Null(grants.Authorize("pane-a", "tab-1", control: true));

        grants.Share("pane-a", "A", "tab-1", "https://github.com", TabShareService.Control);
        grants.Share("pane-a", "A", "tab-2", "https://github.com", TabShareService.Watch);
        grants.Share("pane-b", "B", "tab-1", "https://github.com", TabShareService.Watch);
        Assert.NotNull(grants.Authorize("pane-a", "tab-1", control: true));
        Assert.Null(grants.Authorize("pane-b", "tab-1", control: true));
        Assert.Null(grants.Authorize("pane-a", "tab-2", control: true));

        grants.TakeOver("tab-1");
        Assert.Null(grants.Authorize("pane-a", "tab-1", control: true));
        grants.GiveBack("tab-1");
        grants.Navigated("tab-1", "https://evil.example");
        Assert.Null(grants.Authorize("pane-a", "tab-1", control: true));
        grants.Navigated("tab-1", "https://github.com");
        Assert.NotNull(grants.Authorize("pane-a", "tab-1", control: true));

        grants.Stop("tab-1");
        Assert.Null(grants.Authorize("pane-a", "tab-1", control: true));
        grants.Share("pane-a", "A", "tab-1", "https://github.com", TabShareService.Control);
        grants.TabClosed("tab-1");
        Assert.Null(grants.Authorize("pane-a", "tab-1", control: true));
        grants.Share("pane-a", "A", "tab-1", "https://github.com", TabShareService.Control);
        grants.SessionEnded("pane-a");
        Assert.Null(grants.Authorize("pane-a", "tab-1", control: true));
    }
}
