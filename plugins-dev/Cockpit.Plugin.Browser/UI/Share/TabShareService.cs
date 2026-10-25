using System.Text.Json;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;
using static Cockpit.Plugin.Browser.TabShareContract;

namespace Cockpit.Plugin.Browser.UI.Share;

// Sharing one of Raymond's tabs with one agent session (AC-1714): the consent card that grants it, and the answer to
// every read the backend passes on. The grant is checked on the UI thread right before and right after the read, so
// Stop sharing, closing the tab or a move to another site refuses the very next call.
internal sealed class TabShareService : IDisposable
{
    public const string Watch = "watch";

    public const string Control = "control";

    private static readonly TimeSpan CardTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly ICockpitUiHost _host;
    private readonly BrowserModel _model;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly HashSet<string> _watched = [];

    public TabShareService(ICockpitUiHost host, BrowserModel model, TabGrantRegistry grants)
    {
        _host = host;
        _model = model;
        Grants = grants;
        _subscriptions.Add(host.Channel.Subscribe(Request, channelEvent =>
        {
            if (channelEvent.Payload.Deserialize<TabShareRequest>(Json) is { } request)
            {
                Dispatcher.UIThread.Post(() => _ = _AnswerAsync(request));
            }
        }));
        _subscriptions.Add(host.Channel.Subscribe(SessionClosed, channelEvent =>
        {
            if (channelEvent.Payload.Deserialize<string>(Json) is { } paneId)
            {
                Dispatcher.UIThread.Post(() => Grants.SessionEnded(paneId));
            }
        }));
        model.TabClosed += tab => Grants.TabClosed(tab.Id);
        model.Changed += _WatchNavigation;
    }

    public TabGrantRegistry Grants { get; }

    // The share card: which session, and what it may do. Only an approval with a pick grants anything; Escape, Deny,
    // the timeout and a server without a card all leave the tab unshared.
    public async Task ShareAsync(BrowserTab tab)
    {
        var sessions = await _OpenSessionsAsync();
        if (sessions.Count == 0)
        {
            _host.ShowToast("Start a session first: a tab is shared with one agent session.", PluginToastSeverity.Information);
            return;
        }

        var origin = TabGrantRegistry.OriginOf(tab.Address);
        var request = new ConsentRequest(
            "Share this tab with an agent",
            $"{tab.Address} · your own profile, signed in\n"
            + "This one tab only, until you stop, the tab closes or the session ends.",
            new ConsentSource(null, null, "Browser"),
            "browser.share",
            ConsentRisk.Dangerous)
        {
            Choices =
            [
                new ConsentChoice("session", "With", [.. sessions.Select((session, index) => new ConsentOption(session.PaneId, session.Name, Selected: index == 0))]),
                new ConsentChoice("mode", "What the agent may do",
                [
                    new ConsentOption(Watch, "Watch", "Address, title, page text (accessibility tree) and a screenshot of this one tab.", Selected: true),
                    new ConsentOption("control", "Control", "Click, type, scroll and navigate within this site.", Enabled: false) { Note = "not in this version" },
                ], AllowMultiple: true),
            ],
            ApproveLabel = "Share watching",
            Never = ["Cookies and storage", "saved passwords and autofill", "other tabs", "downloads and uploads", "DevTools"],
        };

        using var timeout = new CancellationTokenSource(CardTimeout);
        var decision = await _host.RequestConsentAsync(request, timeout.Token);
        if (decision.IsApproved == false || decision.Chosen is not { } chosen || chosen.TryGetValue("session", out var picked) == false)
        {
            return;
        }

        var paneId = picked[0];
        var name = sessions.FirstOrDefault(session => session.PaneId == paneId)?.Name ?? paneId;
        if (_model.Tabs.Contains(tab))
        {
            Grants.Share(paneId, name, tab.Id, origin, Watch);
        }
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
    }

    private async Task<IReadOnlyList<OpenCockpitSession>> _OpenSessionsAsync()
    {
        try
        {
            var result = await _host.Channel.InvokeAsync(Sessions, JsonSerializer.SerializeToElement(true, Json));
            return result.Deserialize<List<OpenCockpitSession>>(Json) ?? [];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
    }

    private async Task _AnswerAsync(TabShareRequest request)
    {
        TabShareAnswer answer;
        try
        {
            answer = await _HandleAsync(request);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            answer = _Refuse(request, "The tab could not be read.");
        }

        await _host.Channel.InvokeAsync(Answer, JsonSerializer.SerializeToElement(answer, Json));
    }

    private async Task<TabShareAnswer> _HandleAsync(TabShareRequest request)
    {
        if (request.Tool == "list")
        {
            var tabs = Grants.ForSession(request.PaneId)
                .Select(grant => (grant, tab: _model.Tabs.FirstOrDefault(tab => tab.Id == grant.TabId)))
                .Where(pair => pair.tab is not null)
                .Select(pair => new SharedTabInfo(pair.grant.TabId, pair.tab!.Title, pair.tab.Address, pair.grant.Mode, pair.grant.Paused))
                .ToList();
            return new TabShareAnswer(request.Id, true, Tabs: tabs);
        }

        if (request.TabId is not { } tabId || _Authorized(request.PaneId, tabId) is not { } before)
        {
            return _Refuse(request, "This tab is not shared with your session, or sharing was stopped or paused. Only the operator can share it.");
        }

        var (tab, grant) = before;
        var result = request.Tool switch
        {
            "screenshot" => await _DevToolsAsync(tab, "Page.captureScreenshot", TabReader.ScreenshotParameters),
            _ when request.Mode == "tree" => await _DevToolsAsync(tab, "Accessibility.getFullAXTree", new Dictionary<string, object>()),
            _ => await _DevToolsAsync(tab, "Runtime.evaluate", TabReader.TextParameters),
        };

        // Re-checked after the read: a stop, a close or a move while it ran refuses it, and nothing read goes out.
        if (_Authorized(request.PaneId, tabId) is not { } after || ReferenceEquals(after.Grant, grant) == false)
        {
            return _Refuse(request, "Sharing of this tab ended while it was being read.");
        }

        Grants.MarkRead(grant, DateTimeOffset.Now);
        return request.Tool == "screenshot"
            ? _Answer(request, grant, tab, image: TabReader.Png(result))
            : _Answer(request, grant, tab, text: request.Mode == "tree" ? TabReader.Tree(result) : TabReader.Text(result));
    }

    // The grant of this session on this tab, and only while the tab is still on the origin it was granted for.
    private (BrowserTab Tab, TabGrant Grant)? _Authorized(string paneId, string tabId) =>
        Grants.Authorize(paneId, tabId) is { } grant
        && _model.Tabs.FirstOrDefault(tab => tab.Id == tabId) is { } tab
        && string.Equals(TabGrantRegistry.OriginOf(tab.Address), grant.Origin, StringComparison.OrdinalIgnoreCase)
            ? (tab, grant)
            : null;

    private static async Task<string?> _DevToolsAsync(BrowserTab tab, string method, IReadOnlyDictionary<string, object> parameters)
    {
        try
        {
            return await tab.View.DevToolsAsync(method, parameters).WaitAsync(ReadTimeout);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static TabShareAnswer _Answer(TabShareRequest request, TabGrant grant, BrowserTab tab, string? text = null, string? image = null) =>
        text is null && image is null
            ? _Refuse(request, "The tab did not answer the read.")
            : new TabShareAnswer(request.Id, true, Origin: grant.Origin, Url: tab.Address, Title: tab.Title, Text: text, ImagePng: image);

    private static TabShareAnswer _Refuse(TabShareRequest request, string error) => new(request.Id, false, error);

    // A move to another origin pauses the tab's grants; checked on every change of a tab that has one.
    private void _WatchNavigation()
    {
        foreach (var tab in _model.Tabs.Where(tab => _watched.Add(tab.Id)))
        {
            tab.Changed += () =>
            {
                if (Grants.ForTab(tab.Id).Count > 0)
                {
                    Grants.Navigated(tab.Id, TabGrantRegistry.OriginOf(tab.Address));
                }
            };
        }
    }
}
