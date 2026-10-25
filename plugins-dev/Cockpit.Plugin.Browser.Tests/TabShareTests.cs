using Cockpit.Plugin.Browser.UI.Share;

namespace Cockpit.Plugin.Browser.Tests;

// AC-1714: what an agent may read of Raymond's profile. The tools themselves need CEF and a session; these hold the
// two decisions they go through, the grant check and what a read keeps.
public class TabShareTests
{
    // L2b (authorisation), criteria 1 and 2: a grant is exactly one session on one tab, a move to another site pauses
    // it, and Stop sharing, closing the tab and the session ending each refuse the very next read.
    [Fact]
    public void AGrant_IsOneSessionOnOneTab_AndEndsAtOnceOnStopCloseOrSessionEnd()
    {
        var grants = new TabGrantRegistry();
        Assert.Null(grants.Authorize("pane-a", "tab-1"));

        grants.Share("pane-a", "A", "tab-1", "https://github.com", TabShareService.Watch);
        Assert.NotNull(grants.Authorize("pane-a", "tab-1"));
        Assert.Null(grants.Authorize("pane-b", "tab-1"));
        Assert.Null(grants.Authorize("pane-a", "tab-2"));

        grants.Navigated("tab-1", "https://accounts.google.com");
        Assert.Null(grants.Authorize("pane-a", "tab-1"));
        grants.KeepSharing("tab-1");
        Assert.NotNull(grants.Authorize("pane-a", "tab-1"));

        grants.Stop("tab-1");
        Assert.Null(grants.Authorize("pane-a", "tab-1"));
        grants.Share("pane-a", "A", "tab-1", "https://github.com", TabShareService.Watch);
        grants.TabClosed("tab-1");
        Assert.Null(grants.Authorize("pane-a", "tab-1"));
        grants.Share("pane-a", "A", "tab-1", "https://github.com", TabShareService.Watch);
        grants.SessionEnded("pane-a");
        Assert.Null(grants.Authorize("pane-a", "tab-1"));
    }

    // L2b (boundary), criterion 3: the read script asks the page for its visible text and nothing else, and a result
    // carrying anything besides that text gives the agent the text alone.
    [Fact]
    public void TheReadScript_TouchesNoCookieOrStorage_AndAReadKeepsOnlyThePageText()
    {
        foreach (var forbidden in new[] { "cookie", "Storage", "indexedDB", "caches", "credentials", "password" })
        {
            Assert.DoesNotContain(forbidden, TabReader.TextScript, StringComparison.OrdinalIgnoreCase);
        }

        var text = TabReader.Text("""{"result":{"type":"string","value":"Checks: build failing"},"cookie":"session=known-test-cookie"}""");

        Assert.Equal("Checks: build failing", text);
    }
}
