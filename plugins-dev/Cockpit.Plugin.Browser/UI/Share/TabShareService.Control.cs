using System.Text.Json;
using Cockpit.Plugin.Browser.UI.Engine;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Browser.UI.Share;

// AC-1728: an agent acting in one of the user's tabs. Asking for control opens the share card on the agent's behalf;
// every action needs a control grant of exactly that session on exactly that tab, on the origin it was granted for,
// and goes in as the OSR input the operator's own mouse and keys use. No DevTools Input, no debugging port.
internal sealed partial class TabShareService
{
    public static readonly IReadOnlySet<string> ControlTools = new HashSet<string> { "control", "click", "type", "key", "scroll", "navigate" };

    private const int MaxTyped = 2_000;


    private const string NoControl =
        "You do not control this tab: it was not shared with your session for control, sharing was stopped, the tab moved to another site, or the operator took over. Ask again with request_tab_control.";

    private static readonly string[] NeverCovered = ["Cookies and storage", "saved passwords and autofill", "other tabs", "downloads and uploads", "DevTools"];

    // The element's centre in CSS pixels after scrolling it into view, or null when nothing matches.
    private const string LocateScript =
        "(()=>{const e=document.querySelector(%SELECTOR%);if(!e)return null;e.scrollIntoView({block:'center',inline:'center'});"
        + "const r=e.getBoundingClientRect();return JSON.stringify({x:r.left+r.width/2,y:r.top+r.height/2});})()";

    // The focused field as tag, type and name, for the audit; never its value.
    private const string FocusedFieldScript =
        "(()=>{const e=document.activeElement;if(!e)return '';return e.tagName.toLowerCase()+(e.type?'['+e.type+']':'')+(e.name?' '+e.name:'')})()";

    // What one action did, as the audit and the bar say it. Typed text is never in it, only its length and the field.
    public static string Describe(string tool, string? text, double? x, double? y, string? selector, string? field) => tool switch
    {
        "click" => selector is null ? $"click at {x:0},{y:0}" : $"click {_Short(selector, 80)}",
        "type" => $"type {text?.Length ?? 0} characters" + (string.IsNullOrEmpty(field) ? string.Empty : $" into {_Short(field, 40)}"),
        "key" => $"press {text}",
        "scroll" => $"scroll {x:0},{y:0}",
        "navigate" => $"navigate to {_Short(text ?? string.Empty, 120)}",
        _ => tool,
    };

    private async Task<TabShareAnswer> _ControlAsync(TabShareRequest request)
    {
        if (request.Tool == "control")
        {
            return await _AskControlAsync(request);
        }

        if (request.TabId is not { } tabId || _Controlled(request.PaneId, tabId) is not ({ } tab, { } grant))
        {
            return _Refuse(request, NoControl);
        }

        double? x = request.X, y = request.Y;
        string? field = null;
        string? frames = null;
        if (request.Tool == "click" && request.Selector is { } selector)
        {
            if (_Point(await _DevToolsAsync(tab, "Runtime.evaluate", _Evaluate(LocateScript.Replace("%SELECTOR%", JsonSerializer.Serialize(selector))))) is not { } point)
            {
                return _Refuse(request, "No element on the page matches that selector.");
            }

            (x, y) = point;
        }

        // The frames are noted before the hit test and compared again on CEF's thread right before the click, so a frame
        // added under the point in between refuses it.
        if (request.Tool == "click")
        {
            frames = await tab.View.FrameSignatureAsync();
        }

        if (request.Tool == "click" && (x is null || y is null || frames is null || await _LandsInTopDocumentAsync(tab, (int)x, (int)y) == false))
        {
            return _Refuse(request, "That point is not on this site's own page: it is in a frame, or on nothing. Control covers the top page only.");
        }

        if (request.Tool == "type")
        {
            field = TabReader.Text(await _DevToolsAsync(tab, "Runtime.evaluate", _Evaluate(FocusedFieldScript)));
        }

        // Decided again on CEF's UI thread right before the input: the same grant, the same origin, and for keys a
        // focused frame on that origin. The answer, and so the audit line, comes only after the input went in.
        var needsFocus = request.Tool is "type" or "key";
        bool Gate(string? mainUrl, string? focusedUrl) => ControlPolicy.MayAct(Grants, request.PaneId, tabId, grant, mainUrl, focusedUrl, needsFocus);
        var (done, refusal) = await _ActAsync(request, tab, grant, x, y, Gate, frames);
        if (done == false)
        {
            return _Refuse(request, refusal ?? NoControl);
        }

        var action = Describe(request.Tool, request.Text, x, y, request.Selector, field);
        Grants.MarkAction(grant, action, DateTimeOffset.Now);
        return new TabShareAnswer(request.Id, true, Origin: grant.Origin, Url: tab.Address, Title: tab.Title, Action: action);
    }

    private static async Task<(bool Done, string? Refusal)> _ActAsync(TabShareRequest request, BrowserTab tab, TabGrant grant, double? x, double? y, Func<string?, string?, bool> gate, string? frames)
    {
        switch (request.Tool)
        {
            case "click" when x is { } left && y is { } top && frames is { } signature:
                return (await tab.View.AgentClick((int)left, (int)top, gate, signature), null);
            case "type" when request.Text is { Length: > 0 and <= MaxTyped } text:
                return (await tab.View.AgentType(text, gate), null);
            case "key" when request.Text is { } key:
                var code = KeyCodes.FromName(key);
                return code == 0
                    ? (false, $"Unknown key \"{key}\". Use a name such as Enter, Tab, Escape, Backspace or ArrowDown.")
                    : (await tab.View.AgentKey(code, gate), null);
            case "scroll":
                return (await tab.View.AgentScroll((int)(x ?? 0), (int)(y ?? 0), gate), null);
            case "navigate" when request.Text is { } address:
                return ControlPolicy.SameOrigin(address, grant.Origin)
                    ? (await tab.View.AgentNavigate(address, gate), null)
                    : (false, $"Control covers {grant.Origin} only. Another site needs its own approval: ask again with request_tab_control after the operator opens it.");
            default:
                return (false, $"Missing or invalid arguments for {request.Tool}.");
        }
    }

    // What lies under the point, from DevTools rather than the page's own scripts: an element of the top document.
    private static async Task<bool> _LandsInTopDocumentAsync(BrowserTab tab, int x, int y)
    {
        await _DevToolsAsync(tab, "DOM.enable", new Dictionary<string, object>());
        var node = await _DevToolsAsync(tab, "DOM.getNodeForLocation", new Dictionary<string, object> { ["x"] = x, ["y"] = y });
        var frames = await _DevToolsAsync(tab, "Page.getFrameTree", new Dictionary<string, object>());
        if (_BackendNodeId(node) is not { } backendNodeId)
        {
            return false;
        }

        var described = await _DevToolsAsync(tab, "DOM.describeNode", new Dictionary<string, object> { ["backendNodeId"] = backendNodeId });
        return ControlPolicy.ClickTarget(node, frames, described);
    }

    private static int? _BackendNodeId(string? result)
    {
        try
        {
            using var document = JsonDocument.Parse(result ?? string.Empty);
            return document.RootElement.TryGetProperty("backendNodeId", out var id) ? id.GetInt32() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The share card on the agent's behalf: the asking session fixed, Control picked, its reason shown as its own words.
    private async Task<TabShareAnswer> _AskControlAsync(TabShareRequest request)
    {
        var tab = request.TabId is { } id ? _model.Tabs.FirstOrDefault(tab => tab.Id == id) : _model.Active;
        var session = (await _OpenSessionsAsync()).FirstOrDefault(session => session.PaneId == request.PaneId);
        if (tab is null || session is null || _IsWeb(tab.Address) == false)
        {
            return _Refuse(request, "There is no such web page open in the operator's browser to ask about.");
        }

        var origin = TabGrantRegistry.OriginOf(tab.Address);
        var consent = new ConsentRequest(
            $"{session.Name} asks to control this tab",
            $"{tab.Address} · your own profile, signed in\n"
            + $"The agent says (its own words, not checked): \"{_Short(request.Text ?? string.Empty, 300)}\"\n"
            + "This one tab only, until you stop, the tab closes or the session ends.",
            new ConsentSource(request.PaneId, null, "Browser"),
            "browser.control",
            ConsentRisk.Dangerous)
        {
            Choices =
            [
                new ConsentChoice("session", "With", [new ConsentOption(session.PaneId, session.Name, Selected: true)]),
                new ConsentChoice("mode", "What the agent may do",
                [
                    new ConsentOption(Watch, "Watch", "Address, title, page text (accessibility tree) and a screenshot of this one tab."),
                    new ConsentOption(Control, "Control", $"Watch, plus click, type, scroll and navigate, only within {origin}. Every form submission asks you again.", Selected: true),
                ], AllowMultiple: true),
            ],
            ApproveLabel = "Allow control",
            Never = NeverCovered,
        };

        using var timeout = new CancellationTokenSource(CardTimeout);
        var decision = await _host.RequestConsentAsync(consent, timeout.Token);
        if (decision.IsApproved == false || decision.Chosen?.GetValueOrDefault("mode") is not { Count: > 0 } modes || _model.Tabs.Contains(tab) == false)
        {
            return _Refuse(request, "The operator did not allow control of this tab.");
        }

        var mode = modes.Contains(Control) ? Control : Watch;
        Grants.Share(session.PaneId, session.Name, tab.Id, origin, mode);
        return new TabShareAnswer(request.Id, true, Origin: origin, Url: tab.Address, Title: tab.Title, Text: tab.Id, Action: mode);
    }

    private (BrowserTab Tab, TabGrant Grant)? _Controlled(string paneId, string tabId) =>
        Grants.Authorize(paneId, tabId, control: true) is { } grant
        && _model.Tabs.FirstOrDefault(tab => tab.Id == tabId) is { } tab
        && string.Equals(TabGrantRegistry.OriginOf(tab.Address), grant.Origin, StringComparison.OrdinalIgnoreCase)
            ? (tab, grant)
            : null;

    // Send in the form bar: the held request goes on only while the tab still has a controller on its target's origin.
    public static void SendHeld(TabGrantRegistry grants, BrowserTab tab)
    {
        var held = grants.TakeHeld(tab.Id);
        var origin = grants.ForTab(tab.Id).FirstOrDefault(grant => grant.Controls)?.Origin;
        if (held is not null && origin is not null && ControlPolicy.SameOrigin(_Target(held), origin))
        {
            tab.View.ReleaseHeldSubmit();
            return;
        }

        tab.View.DropHeldSubmit();
    }

    public static void DropHeld(TabGrantRegistry grants, BrowserTab tab)
    {
        grants.TakeHeld(tab.Id);
        tab.View.DropHeldSubmit();
    }

    // The view already held it in CEF; another origin never reaches this, it was cancelled there.
    private void _SubmitHeld(BrowserTab tab, string method, string url) => Grants.Hold(tab.Id, $"{method} {url}");

    // Each view holds submissions and refuses file pickers exactly while its tab has a controller, on its origin.
    private void _SyncControlled()
    {
        foreach (var tab in _model.Tabs)
        {
            var origin = Grants.ForTab(tab.Id).FirstOrDefault(grant => grant.Controls)?.Origin;
            tab.View.ControlOrigin = origin;
            if (origin is null)
            {
                DropHeld(Grants, tab);
            }
        }
    }

    private static string _Target(string submit) => submit[(submit.IndexOf(' ') + 1)..];

    // Your own click or key in a controlled tab pauses the agent there until you give control back, and drops
    // whatever submission was waiting, so nothing the agent started can be sent on your later click.
    private void _OperatorInput(BrowserTab tab)
    {
        if (Grants.ForTab(tab.Id).Any(grant => grant.Controls && grant.OperatorHolds == false))
        {
            Grants.TakeOver(tab.Id);
            DropHeld(Grants, tab);
        }
    }

    private static bool _IsWeb(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static Dictionary<string, object> _Evaluate(string expression) => new() { ["expression"] = expression, ["returnByValue"] = true };

    private static (double X, double Y)? _Point(string? evaluateResult)
    {
        if (TabReader.Text(evaluateResult) is not { } json)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return (document.RootElement.GetProperty("x").GetDouble(), document.RootElement.GetProperty("y").GetDouble());
    }

    private static string _Short(string text, int max) => text.Length > max ? text[..max] + "…" : text;
}
