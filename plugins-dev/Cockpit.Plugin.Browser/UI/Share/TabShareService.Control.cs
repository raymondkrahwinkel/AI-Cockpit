using System.Text.Json;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Browser.UI.Share;

// AC-1728: an agent acting in one of Raymond's tabs. Asking for control opens the share card on the agent's behalf;
// every action needs a control grant of exactly that session on exactly that tab, on the origin it was granted for,
// and goes in as the OSR input the operator's own mouse and keys use. No DevTools Input, no debugging port.
internal sealed partial class TabShareService
{
    public static readonly IReadOnlySet<string> ControlTools = new HashSet<string> { "control", "click", "type", "key", "scroll", "navigate" };

    private const int MaxTyped = 2_000;

    private readonly Dictionary<string, string> _heldForms = [];

    private const string NoControl =
        "You do not control this tab: it was not shared with your session for control, sharing was stopped, the tab moved to another site, or the operator took over. Ask again with request_tab_control.";

    private static readonly string[] NeverCovered = ["Cookies and storage", "saved passwords and autofill", "other tabs", "downloads and uploads", "DevTools"];

    // The element's centre in CSS pixels after scrolling it into view, or null when nothing matches.
    private const string LocateScript =
        "(()=>{const e=document.querySelector(%SELECTOR%);if(!e)return null;e.scrollIntoView({block:'center',inline:'center'});"
        + "const r=e.getBoundingClientRect();return JSON.stringify({x:r.left+r.width/2,y:r.top+r.height/2});})()";

    private const string FocusIsPasswordScript = "(()=>{const e=document.activeElement;return !!e&&e.type==='password'})()";

    // What one action did, as the audit and the bar say it: typed text cut short, and never shown for a password field.
    public static string Describe(string tool, string? text, double? x, double? y, string? selector, bool password) => tool switch
    {
        "click" => selector is null ? $"click at {x:0},{y:0}" : $"click {_Short(selector, 80)}",
        "type" => password ? "type •••• into a password field" : $"type \"{_Short(text ?? string.Empty, 40)}\"",
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

        if (request.TabId is not { } tabId || _Controlled(request.PaneId, tabId) is null)
        {
            return _Refuse(request, NoControl);
        }

        double? x = request.X, y = request.Y;
        var password = false;
        if (request.Tool == "click" && request.Selector is { } selector)
        {
            var tab = _model.Tabs.First(tab => tab.Id == tabId);
            var located = _Point(await _DevToolsAsync(tab, "Runtime.evaluate", _Evaluate(LocateScript.Replace("%SELECTOR%", JsonSerializer.Serialize(selector)))));
            if (located is not { } point)
            {
                return _Refuse(request, "No element on the page matches that selector.");
            }

            (x, y) = point;
        }
        else if (request.Tool == "type")
        {
            var tab = _model.Tabs.First(tab => tab.Id == tabId);
            password = _MaybePassword(await _DevToolsAsync(tab, "Runtime.evaluate", _Evaluate(FocusIsPasswordScript)));
        }

        // Checked again after the awaits above, and nothing awaits between this check and the input going in.
        if (_Controlled(request.PaneId, tabId) is not ({ } target, { } grant))
        {
            return _Refuse(request, NoControl);
        }

        var refusal = _Act(request, target, grant, x, y);
        if (refusal is not null)
        {
            return _Refuse(request, refusal);
        }

        var action = Describe(request.Tool, request.Text, x, y, request.Selector, password);
        Grants.MarkAction(grant, action);
        return new TabShareAnswer(request.Id, true, Origin: grant.Origin, Url: target.Address, Title: target.Title, Action: action);
    }

    private static string? _Act(TabShareRequest request, BrowserTab tab, TabGrant grant, double? x, double? y)
    {
        switch (request.Tool)
        {
            case "click" when x is { } left && y is { } top:
                tab.View.AgentClick((int)left, (int)top);
                return null;
            case "type" when request.Text is { Length: > 0 and <= MaxTyped } text:
                tab.View.AgentType(text);
                return null;
            case "key" when request.Text is { } key:
                return tab.View.AgentKey(key) ? null : $"Unknown key \"{key}\". Use a name such as Enter, Tab, Escape, Backspace or ArrowDown.";
            case "scroll":
                tab.View.AgentScroll((int)(x ?? 0), (int)(y ?? 0));
                return null;
            case "navigate" when request.Text is { } address:
                if (_SameOrigin(address, grant.Origin) == false)
                {
                    return $"Control covers {grant.Origin} only. Another site needs its own approval: ask again with request_tab_control after the operator opens it.";
                }

                tab.View.Navigate(address);
                return null;
            default:
                return $"Missing or invalid arguments for {request.Tool}.";
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

    // A form held in a controlled tab, per tab id, as "<method> <target address>"; the bar asks Raymond about it.
    public IReadOnlyDictionary<string, string> HeldForms => _heldForms;

    public event Action? HeldFormsChanged;

    // Only while the tab is still controlled and the form still targets the granted origin.
    public void SendHeldForm(string tabId)
    {
        if (_heldForms.Remove(tabId, out var submit) && _model.Tabs.FirstOrDefault(tab => tab.Id == tabId) is { } tab)
        {
            var grant = Grants.ForTab(tabId).FirstOrDefault(grant => grant.Controls);
            if (grant is not null && _SameOrigin(_Target(submit), grant.Origin))
            {
                tab.View.ReleaseHeldSubmit();
            }
            else
            {
                tab.View.DropHeldSubmit();
            }

            HeldFormsChanged?.Invoke();
        }
    }

    public void DropHeldForm(string tabId)
    {
        if (_heldForms.Remove(tabId) && _model.Tabs.FirstOrDefault(tab => tab.Id == tabId) is { } tab)
        {
            tab.View.DropHeldSubmit();
            HeldFormsChanged?.Invoke();
        }
    }

    // A submission to another origin is dropped without asking; one to the granted origin waits for Raymond.
    private void _SubmitHeld(BrowserTab tab, string submit)
    {
        var grant = Grants.ForTab(tab.Id).FirstOrDefault(grant => grant.Controls);
        if (grant is not null && _SameOrigin(_Target(submit), grant.Origin))
        {
            _heldForms[tab.Id] = submit;
        }
        else
        {
            _heldForms.Remove(tab.Id);
            tab.View.DropHeldSubmit();
        }

        HeldFormsChanged?.Invoke();
    }

    // The view holds forms and refuses file pickers exactly while its tab has a controller.
    private void _SyncControlled()
    {
        foreach (var tab in _model.Tabs)
        {
            var controlled = Grants.ForTab(tab.Id).Any(grant => grant.Controls);
            if (tab.View.Controlled != controlled)
            {
                tab.View.Controlled = controlled;
            }

            if (controlled == false && _heldForms.Remove(tab.Id))
            {
                tab.View.DropHeldSubmit();
                HeldFormsChanged?.Invoke();
            }
        }
    }

    private static string _Target(string submit) => submit[(submit.IndexOf(' ') + 1)..];

    // Raymond's own click or key in a controlled tab pauses the agent there until he gives control back.
    private void _OperatorInput(BrowserTab tab)
    {
        if (Grants.ForTab(tab.Id).Any(grant => grant.Controls && grant.OperatorHolds == false))
        {
            Grants.TakeOver(tab.Id);
        }
    }

    private static bool _IsWeb(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static bool _SameOrigin(string address, string origin) =>
        _IsWeb(address)
        && string.Equals(TabGrantRegistry.OriginOf(address), origin, StringComparison.OrdinalIgnoreCase);

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

    // Fails closed: only a clear "not a password field" shows the typed text in the audit.
    private static bool _MaybePassword(string? evaluateResult)
    {
        try
        {
            using var document = JsonDocument.Parse(evaluateResult ?? "{}");
            return (document.RootElement.TryGetProperty("result", out var result)
                && result.TryGetProperty("value", out var value)
                && value.ValueKind == JsonValueKind.False) == false;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static string _Short(string text, int max) => text.Length > max ? text[..max] + "…" : text;
}
