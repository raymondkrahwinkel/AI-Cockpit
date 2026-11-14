using System.Text.Json;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;
using static Cockpit.Plugin.Browser.TabShareContract;

namespace Cockpit.Plugin.Browser.UI.Share;

// Sharing one of the user's tabs with one agent session (AC-1714): the consent card that grants it, and the answer to
// every read the backend passes on. The grant is checked on the UI thread right before and right after the read, so
// Stop sharing, closing the tab or a move to another site refuses the very next call.
internal sealed partial class TabShareService : IDisposable
{
    public const string Watch = "watch";

    public const string Control = "control";

    private static readonly TimeSpan CardTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly ICockpitUiHost _host;
    private readonly BrowserModel _model;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Dictionary<string, Action> _watched = [];
    private readonly Dictionary<string, (string? PaneId, CancellationTokenSource Cancel)> _pending = [];
    private bool _disposed;

    public TabShareService(ICockpitUiHost host, BrowserModel model, TabGrantRegistry grants)
    {
        _host = host;
        _model = model;
        Grants = grants;
        _subscriptions.Add(host.Channel.Subscribe(Request, channelEvent =>
        {
            if (channelEvent.Payload.Deserialize<TabShareRequest>(Json) is { } request)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed == false)
                    {
                        _ = _AnswerAsync(request);
                    }
                });
            }
        }));
        _subscriptions.Add(host.Channel.Subscribe(SessionClosed, channelEvent =>
        {
            if (channelEvent.Payload.Deserialize<string>(Json) is { } paneId)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _CancelPending(paneId);
                    Grants.SessionEnded(paneId);
                });
            }
        }));
        model.TabClosed += _TabClosed;
        model.Changed += _WatchNavigation;
        Grants.Changed += _GrantsChanged;
        _WatchNavigation();
    }

    public TabGrantRegistry Grants { get; }

    // The share card: which session, and what it may do. Only an approval with a pick grants anything; Escape, Deny,
    // the timeout and a server without a card all leave the tab unshared.
    public async Task ShareAsync(BrowserTab tab)
    {
        using var pending = _NewPending();
        var id = Guid.NewGuid().ToString("N");
        if (_disposed || tab.View.HostStopped.IsCancellationRequested)
        {
            return;
        }
        _pending.Add(id, (null, pending));
        try
        {
            await _ShareAsync(tab, pending.Token);
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested)
        {
        }
        finally
        {
            _pending.Remove(id);
        }
    }

    private async Task _ShareAsync(BrowserTab tab, CancellationToken cancellationToken)
    {
        var sessions = await _OpenSessionsAsync(cancellationToken);
        if (sessions.Count == 0)
        {
            _host.ShowToast("No session has the browser tools (cockpit-browser). Start one with them, or give a running one a restart with them.", PluginToastSeverity.Information);
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
                    new ConsentOption(Control, "Control", $"Watch, plus click, type, scroll and navigate, only within {origin}. Every form submission asks you again."),
                ], AllowMultiple: true),
            ],
            ApproveLabel = "Share watching",
            Never = NeverCovered,
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CardTimeout);
        var decision = await _host.RequestConsentAsync(request, timeout.Token).WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (decision.IsApproved == false || decision.Chosen is not { } chosen || chosen.TryGetValue("session", out var picked) == false)
        {
            return;
        }

        var paneId = picked[0];
        var name = sessions.FirstOrDefault(session => session.PaneId == paneId)?.Name ?? paneId;
        var mode = chosen.TryGetValue("mode", out var modes) && modes.Contains(Control) ? Control : Watch;
        if (_model.Tabs.Contains(tab) && tab.View.HostStopped.IsCancellationRequested == false)
        {
            Grants.Share(paneId, name, tab.Id, origin, mode);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _CancelPending();
        Grants.Clear();
        _SyncControlled();
        _model.TabClosed -= _TabClosed;
        _model.Changed -= _WatchNavigation;
        Grants.Changed -= _GrantsChanged;
        foreach (var unsubscribe in _watched.Values)
        {
            unsubscribe();
        }
        _watched.Clear();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
    }

    private async Task<IReadOnlyList<OpenCockpitSession>> _OpenSessionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _host.Channel.InvokeAsync(Sessions, JsonSerializer.SerializeToElement(true, Json), cancellationToken).WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result.Deserialize<List<OpenCockpitSession>>(Json) ?? [];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
    }

    private async Task _AnswerAsync(TabShareRequest request)
    {
        using var pending = _NewPending();
        _pending.Add(request.Id, (request.PaneId, pending));
        TabShareAnswer answer;
        try
        {
            answer = await _HandleAsync(request, pending.Token).WaitAsync(pending.Token);
            pending.Token.ThrowIfCancellationRequested();
            await _host.Channel.InvokeAsync(Answer, JsonSerializer.SerializeToElement(answer, Json));
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested)
        {
            await _NotifyCancelledAsync([request.Id]);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            answer = _Refuse(request, "The tab could not be read.");
            if (pending.IsCancellationRequested == false)
            {
                await _host.Channel.InvokeAsync(Answer, JsonSerializer.SerializeToElement(answer, Json));
            }
        }
        finally
        {
            _pending.Remove(request.Id);
        }
    }

    private async Task<TabShareAnswer> _HandleAsync(TabShareRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ControlTools.Contains(request.Tool))
        {
            return await _ControlAsync(request, cancellationToken);
        }

        if (request.Tool == "list")
        {
            var tabs = Grants.ForSession(request.PaneId)
                .Select(grant => (grant, tab: _model.Tabs.FirstOrDefault(tab => tab.Id == grant.TabId)))
                .Where(pair => pair.tab is not null)
                .Select(pair => new SharedTabInfo(pair.grant.TabId, pair.tab!.Title, pair.tab.Address, pair.grant.Mode, pair.grant.Paused))
                .ToList();
            return new TabShareAnswer(request.Id, true, Tabs: tabs);
        }

        if (request.Tool == "request")
        {
            return await TabLookAlong.AskAsync(
                request.PaneId,
                request.Text,
                [.. _model.Tabs.Where(tab => tab.View.HostStopped.IsCancellationRequested == false).Select(tab => new LookAlongTab(tab.Id, tab.Title, tab.Address))],
                _model.Active?.Id,
                async card =>
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(CardTimeout);
                    var decision = await _host.RequestConsentAsync(card, timeout.Token).WaitAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    return decision;
                },
                tabId => _ShareAndReadAsync(request, tabId, cancellationToken),
                error => _Refuse(request, error));
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

        cancellationToken.ThrowIfCancellationRequested();

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

    // After the click: the grant (this session, this tab, watching) and the first read through the same path as any other.
    private async Task<TabShareAnswer> _ShareAndReadAsync(TabShareRequest request, string tabId, CancellationToken cancellationToken)
    {
        if (_model.Tabs.FirstOrDefault(tab => tab.Id == tabId) is not { } tab)
        {
            return _Refuse(request, "The tab was closed before it could be shared.");
        }

        var session = (await _OpenSessionsAsync(cancellationToken)).FirstOrDefault(session => session.PaneId == request.PaneId);
        cancellationToken.ThrowIfCancellationRequested();
        if (session is null || tab.View.HostStopped.IsCancellationRequested)
        {
            return _Refuse(request, "The session or browser stopped before the tab could be shared.");
        }
        var name = session.Name;
        Grants.Share(request.PaneId, name, tabId, TabGrantRegistry.OriginOf(tab.Address), Watch);
        return await _HandleAsync(request with { Tool = "screenshot", TabId = tabId }, cancellationToken);
    }

    // The grant of this session on this tab, and only while the tab is still on the origin it was granted for.
    private (BrowserTab Tab, TabGrant Grant)? _Authorized(string paneId, string tabId) =>
        Grants.Authorize(paneId, tabId) is { } grant
        && _model.Tabs.FirstOrDefault(tab => tab.Id == tabId) is { } tab
        && tab.View.HostStopped.IsCancellationRequested == false
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
            : new TabShareAnswer(request.Id, true, Origin: grant.Origin, Url: tab.Address, Title: tab.Title, Text: text, ImagePng: image, TabId: grant.TabId);

    private static TabShareAnswer _Refuse(TabShareRequest request, string error) => new(request.Id, false, error);

    // A move to another origin pauses the tab's grants; checked on every change of a tab that has one.
    private void _WatchNavigation()
    {
        foreach (var tab in _model.Tabs.Where(tab => _watched.ContainsKey(tab.Id) == false))
        {
            Action input = () => _OperatorInput(tab);
            Action<string, string> submit = (method, url) => _SubmitHeld(tab, method, url);
            Action changed = () =>
            {
                if (Grants.ForTab(tab.Id).Count > 0)
                {
                    Grants.Navigated(tab.Id, TabGrantRegistry.OriginOf(tab.Address));
                }
            };
            tab.View.OperatorInput += input;
            tab.View.SubmitHeld += submit;
            tab.Changed += changed;
            var stopped = tab.View.HostStopped.Register(() => Dispatcher.UIThread.Post(() =>
            {
                if (_disposed == false)
                {
                    _CancelPending();
                    Grants.Clear();
                }
            }));
            _watched.Add(tab.Id, () =>
            {
                stopped.Dispose();
                tab.View.OperatorInput -= input;
                tab.View.SubmitHeld -= submit;
                tab.Changed -= changed;
            });
        }
    }

    private void _GrantsChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed == false)
        {
            _SyncControlled();
        }
    });

    private void _TabClosed(BrowserTab tab)
    {
        _CancelPending();
        Grants.TabClosed(tab.Id);
        if (_watched.Remove(tab.Id, out var unsubscribe))
        {
            unsubscribe();
        }
    }

    private void _CancelPending(string? paneId = null)
    {
        var pending = _pending.Where(pair => paneId is null || pair.Value.PaneId is null || pair.Value.PaneId == paneId).ToArray();
        foreach (var pair in pending)
        {
            pair.Value.Cancel.Cancel();
        }
    }

    private CancellationTokenSource _NewPending()
    {
        CancellationToken[] tokens = [.. _model.Tabs.Select(tab => tab.View.HostStopped)
            .Where(token => token.CanBeCanceled && token.IsCancellationRequested == false)];
        return tokens.Length == 0 ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(tokens);
    }

    private async Task _NotifyCancelledAsync(string[] ids)
    {
        try
        {
            await _host.Channel.InvokeAsync(Cancel, JsonSerializer.SerializeToElement(ids, Json));
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // The backend also cancels its pending calls when its bridge is disposed.
        }
    }
}
