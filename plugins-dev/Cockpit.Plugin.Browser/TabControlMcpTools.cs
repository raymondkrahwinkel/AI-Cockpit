using System.ComponentModel;
using ModelContextProtocol.Server;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Browser;

// AC-1728: acting in a tab of the operator's own profile. Only after the operator allowed control for this session on
// this tab; the UI half decides every action. Each action that went in lands in the consent audit.
internal sealed partial class TabShareMcpTools
{
    // The share card waits two minutes for the operator; this covers it and the round trip.
    private static readonly TimeSpan ControlCardTimeout = TimeSpan.FromMinutes(2.5);

    [McpServerTool(Name = "request_tab_control", Destructive = false)]
    [Description("Asks the operator to let your session control one of their browser tabs: click, type, scroll and navigate within that tab's site, in their own signed-in profile. Opens an approval card and waits for the answer (up to two minutes). Without tabId it asks about the tab the operator has in view. Give a short, honest reason; it is shown to the operator as your own words. Never covers cookies, saved passwords, other tabs, downloads, uploads or DevTools, and every form submission asks the operator again.")]
    public async Task<string> RequestTabControl(
        [Description("Why you need control, in one sentence.")] string reason,
        [Description("The tab id from browser_list_shared_tabs; omit for the tab in view.")] string? tabId = null)
    {
        if (_Caller() is not { } caller)
        {
            return _Refusal(NoCaller);
        }

        var answer = await bridge.AskAsync(new TabShareRequest(Guid.NewGuid().ToString("N"), caller, "control", tabId, Text: reason), ControlCardTimeout).ConfigureAwait(false);
        return answer.Ok ? _Serialize(new { ok = true, tabId = answer.Text, mode = answer.Action, url = answer.Url, title = answer.Title }) : _Refusal(answer.Error);
    }

    [McpServerTool(Name = "browser_click", Destructive = true)]
    [Description("Clicks in a tab your session controls, at x,y in CSS pixels of the visible page, or on the first element matching a CSS selector (scrolled into view first). Refused without control of that tab.")]
    public Task<string> Click(
        [Description("The tab id.")] string tabId,
        [Description("X in CSS pixels of the visible page.")] double? x = null,
        [Description("Y in CSS pixels of the visible page.")] double? y = null,
        [Description("A CSS selector, instead of x and y.")] string? selector = null) =>
        _ActAsync("browser_click", new TabShareRequest(string.Empty, string.Empty, "click", tabId, X: x, Y: y, Selector: selector));

    [McpServerTool(Name = "browser_type", Destructive = true)]
    [Description("Types text into the focused element of a tab your session controls (click the field first). Refused without control of that tab.")]
    public Task<string> Type([Description("The tab id.")] string tabId, [Description("The text to type, at most 2000 characters.")] string text) =>
        _ActAsync("browser_type", new TabShareRequest(string.Empty, string.Empty, "type", tabId, Text: text));

    [McpServerTool(Name = "browser_press_key", Destructive = true)]
    [Description("Presses one key in a tab your session controls: Enter, Tab, Escape, Backspace, Delete, ArrowUp, ArrowDown, ArrowLeft, ArrowRight, PageUp, PageDown, Home, End, Space or a letter. Refused without control of that tab.")]
    public Task<string> PressKey([Description("The tab id.")] string tabId, [Description("The key name.")] string key) =>
        _ActAsync("browser_press_key", new TabShareRequest(string.Empty, string.Empty, "key", tabId, Text: key));

    [McpServerTool(Name = "browser_scroll", Destructive = true)]
    [Description("Scrolls a tab your session controls by dx,dy wheel units (120 is one notch; negative dy scrolls down). Refused without control of that tab.")]
    public Task<string> Scroll([Description("The tab id.")] string tabId, [Description("Horizontal wheel delta.")] int dx, [Description("Vertical wheel delta.")] int dy) =>
        _ActAsync("browser_scroll", new TabShareRequest(string.Empty, string.Empty, "scroll", tabId, X: dx, Y: dy));

    [McpServerTool(Name = "browser_navigate", Destructive = true)]
    [Description("Opens an http(s) address in your own tab, including another site. Shared operator tabs stay on the site (scheme, host and port) control was allowed for; another site requires a new grant.")]
    public Task<string> Navigate([Description("The tab id.")] string tabId, [Description("An http or https address on the same site.")] string url) =>
        _ActAsync("browser_navigate", new TabShareRequest(string.Empty, string.Empty, "navigate", tabId, Text: url));

    private async Task<string> _ActAsync(string tool, TabShareRequest action)
    {
        if (_Caller() is not { } caller)
        {
            return _Refusal(NoCaller);
        }

        if (action.TabId is { } tabId && AgentOwnedTabs.IsOwnedId(tabId))
        {
            var result = owned is null ? new TabShareAnswer("", false, "browser_unavailable")
                : await owned.ActAsync(caller, action).ConfigureAwait(false);
            return result.Ok ? _Serialize(new { ok = true, done = result.Action }) : _Refusal(result.Error);
        }

        var answer = await bridge.AskAsync(action with { Id = Guid.NewGuid().ToString("N"), PaneId = caller }, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (answer.Ok == false)
        {
            return _Refusal(answer.Error);
        }

        await host.RequestConsentAsync(new ConsentRequest(
            "An agent acted in a browser tab it controls",
            $"{tool} · tab {action.TabId} · {answer.Origin} · {answer.Action}",
            new ConsentSource(caller, null, "Browser"),
            "browser." + tool,
            ConsentRisk.LowRisk,
            PreApprovedBy: $"tab control grant {action.TabId}")).ConfigureAwait(false);
        return _Serialize(new { ok = true, done = answer.Action, url = answer.Url, title = answer.Title });
    }
}
