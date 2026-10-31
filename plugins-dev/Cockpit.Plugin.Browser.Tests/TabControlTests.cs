using Cockpit.Plugin.Browser.UI.Share;

namespace Cockpit.Plugin.Browser.Tests;

// AC-1728: when an agent may act in one of Raymond's tabs. Every control tool goes through this one check.
public class TabControlTests
{
    // L2b (authorisation), criteria 1 to 4: watching is not enough, control is exactly one session on one tab, and
    // Raymond taking over, Stop, closing the tab, the session ending or a move to another site refuse the next action;
    // and the audit masks a password field (criterion 5). The review's points 1 to 4 each have a block at the end.
    [Fact]
    public async Task Control_IsOneSessionOnOneTab_AndNeverFollowsFromWatching()
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

        // Criterion 5: the audit line of a typed text shows it, except in a password field.
        Assert.Equal("type 5 characters into input[text] q", TabShareService.Describe("type", "hello", null, null, null, "input[text] q"));
        Assert.DoesNotContain("test-only-secret", TabShareService.Describe("type", "test-only-secret", null, null, null, "input[password]"));

        // Review 1: the gate on CEF's thread holds only the very grant that authorized the action.
        grants.Share("pane-a", "A", "tab-1", "https://github.com", TabShareService.Control);
        var grant = grants.Authorize("pane-a", "tab-1", control: true) ?? throw new InvalidOperationException("no grant");
        const string page = "https://github.com/pr/1";
        Assert.True(ControlPolicy.MayAct(grants, "pane-a", "tab-1", grant, page, page, needsFocus: true));
        grants.Stop("tab-1");
        grants.Share("pane-a", "A", "tab-1", "https://github.com", TabShareService.Control);
        Assert.False(ControlPolicy.MayAct(grants, "pane-a", "tab-1", grant, page, page, needsFocus: true));

        // Review 2: keys never go to a focused frame of another site, and a click never lands in a frame.
        grant = grants.Authorize("pane-a", "tab-1", control: true) ?? throw new InvalidOperationException("no grant");
        Assert.False(ControlPolicy.MayAct(grants, "pane-a", "tab-1", grant, page, "https://evil.example/frame", needsFocus: true));
        const string tree = """{"frameTree":{"frame":{"id":"MAIN"}}}""";
        Assert.True(ControlPolicy.ClickTarget("""{"backendNodeId":7,"frameId":"MAIN"}""", tree, """{"node":{"nodeName":"BUTTON"}}"""));
        Assert.False(ControlPolicy.ClickTarget("""{"backendNodeId":7,"frameId":"MAIN"}""", tree, """{"node":{"nodeName":"IFRAME"}}"""));
        Assert.False(ControlPolicy.ClickTarget("""{"backendNodeId":7,"frameId":"CHILD"}""", tree, """{"node":{"nodeName":"BUTTON"}}"""));

        // Reviews 3 and 4: decided in CEF, not the page. Any form submission waits, GET or POST, a script's POST too, and
        // another site is dropped, the top frame leaving the origin included; a plain same-site link is not held, a
        // frame may load another site, the address bar may leave, and nothing is held without a controller.
        const string origin = "https://github.com";
        Assert.Equal(ControlPolicy.Verdict.Hold, ControlPolicy.Navigation(origin, "GET", formSubmit: true, "https://github.com/search?q=x", mainFrame: true, fromHost: false));
        Assert.Equal(ControlPolicy.Verdict.Hold, ControlPolicy.Navigation(origin, "POST", formSubmit: false, "https://github.com/merge", mainFrame: true, fromHost: false));
        Assert.Equal(ControlPolicy.Verdict.Drop, ControlPolicy.Navigation(origin, "POST", formSubmit: true, "https://evil.example/collect", mainFrame: false, fromHost: false));
        Assert.Equal(ControlPolicy.Verdict.Drop, ControlPolicy.Navigation(origin, "GET", formSubmit: false, "https://evil.example/?d=secret", mainFrame: true, fromHost: false));
        Assert.Equal(ControlPolicy.Verdict.Allow, ControlPolicy.Navigation(origin, "GET", formSubmit: false, "https://github.com/pr/2", mainFrame: true, fromHost: false));
        Assert.Equal(ControlPolicy.Verdict.Allow, ControlPolicy.Navigation(origin, "GET", formSubmit: false, "https://youtube.com/embed/x", mainFrame: false, fromHost: false));
        Assert.Equal(ControlPolicy.Verdict.Allow, ControlPolicy.Navigation(origin, "GET", formSubmit: false, "https://news.example", mainFrame: true, fromHost: true));
        Assert.Equal(ControlPolicy.Verdict.Allow, ControlPolicy.Navigation(null, "POST", formSubmit: true, "https://github.com/merge", mainFrame: true, fromHost: false));

        // AC-1764: TabShareService answers every Changed with a TakeHeld per tab, so taking nothing must not signal one;
        // it did, and a single session ending re-posted itself until Cockpit's UI thread starved. Taking a held one does.
        var changes = 0;
        grants.Changed += () => changes++;
        Assert.Null(grants.TakeHeld("tab-1"));
        Assert.Equal(0, changes);
        grants.Hold("tab-1", "POST https://github.com/merge");
        Assert.Equal("POST https://github.com/merge", grants.TakeHeld("tab-1"));
        Assert.Equal(2, changes);
        Assert.False(ControlPolicy.SameOrigin("https://github.com:8443/", origin));
        Assert.False(ControlPolicy.SameOrigin("http://github.com/", origin));
        var host = System.Reflection.DispatchProxy.Create<Cockpit.Plugins.Abstractions.ICockpitHost, AppKeyHost>();
        ((AppKeyHost)(object)host).Caller = "pane-a";
        using var bridge = new TabShareBridge(host);
        var tools = new TabShareMcpTools(host, bridge);
        Assert.Contains("not_owner:", await tools.CloseTab("tab-1"));
        Assert.Contains("not_owner:", await tools.WaitFor("tab-1", selector: "body"));
    }
}
