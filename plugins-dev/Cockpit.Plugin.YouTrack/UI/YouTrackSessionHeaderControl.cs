using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.YouTrack.UI;

// The issue a session is working on, in that session's own header bar (#75): its id and status, and a menu with
// the moves the board actually allows. Which session matters here — the cockpit shows several at once, and the
// ticket you are working on in one pane says nothing about the other three — so this is bound to its own pane
// via `IPluginSessionContext.PaneId` rather than following the selection.
// Shows nothing at all until an issue is linked: an empty indicator in every header is noise.
internal sealed class YouTrackSessionHeaderControl : UserControl
{
    private readonly ICockpitUiHost _host;
    private readonly IPluginSessionContext _session;
    private readonly YouTrackBackend _backend;
    private readonly YouTrackSettings _settings;

    private readonly TextBlock _label;
    private readonly Button _row;

    private LinkedIssue? _link;
    private YouTrackIssueFields? _fields;
    private int _loadToken;
    private IDisposable? _linkChanged;

    public YouTrackSessionHeaderControl(ICockpitUiHost host, IPluginSessionContext session, YouTrackBackend backend, YouTrackSettings settings)
    {
        _host = host;
        _session = session;
        _backend = backend;
        _settings = settings;

        _label = new TextBlock { FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        _row = new Button
        {
            Padding = new Thickness(6, 1),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children =
                {
                    new MaterialIcon { Kind = MaterialIconKind.TicketOutline, Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center },
                    _label,
                },
            },
        };
        // The badge is what this session has to *say*, so it says nothing when there is no ticket: picking one is an
        // action, and actions live in the header's own menu, where every plugin's fit in the room of one button.
        _row.Click += (_, _) => _ShowMenu();

        Content = _row;
        IsVisible = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _linkChanged = _backend.OnLinkChanged(_OnLinkChanged);
        _ = _LoadAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _linkChanged?.Dispose();
        _linkChanged = null;
    }

    // Raised on the backend's publishing thread, not the UI thread — marshal before touching a control.
    private void _OnLinkChanged(string paneId)
    {
        if (string.Equals(paneId, _session.PaneId, StringComparison.Ordinal))
        {
            Dispatcher.UIThread.Post(() => _ = _LoadAsync());
        }
    }

    // Re-reads the linked issue's status from YouTrack: what it is worth now, and what it may become. Only the
    // latest load wins — clicking through the menu can start a second one before the first returns.
    private async Task _LoadAsync()
    {
        var token = ++_loadToken;
        LinkedIssue? link;
        try
        {
            link = await _backend.LinkedAsync(_session.PaneId);
        }
        catch (Exception)
        {
            // The badge keeps its last-known state; the next link change tries again.
            return;
        }

        if (token != _loadToken)
        {
            return;
        }

        _link = link;
        if (link is null)
        {
            _fields = null;
            IsVisible = false;
            return;
        }

        _label.Text = link.Issue.IdReadable;
        IsVisible = true;

        try
        {
            var fields = await _backend.IssueFieldsAsync(link.Instance, link.Issue);
            if (token != _loadToken)
            {
                return;
            }

            _fields = fields;
            _Render(link, fields.State?.CurrentValue);
        }
        catch (Exception exception)
        {
            if (token != _loadToken)
            {
                return;
            }

            _fields = null;
            _label.Text = link.Issue.IdReadable;
            ToolTip.SetTip(_row, $"{link.Issue.Summary}\n\nCould not read the status: {exception.Message}");
        }
    }

    private void _Render(LinkedIssue link, string? state)
    {
        _label.Text = state is { Length: > 0 } ? $"{link.Issue.IdReadable} · {state}" : link.Issue.IdReadable;
        ToolTip.SetTip(_row, $"{link.Issue.Summary}\n{link.Instance.Label}\n\nClick for actions.");
    }

    // Opens the picker for one pane — what the header menu's "Track a YouTrack issue" runs. Linking from the big dialog links to whichever session is selected, which is a guess as soon as four panes are open.
    public static void Pick(ICockpitUiHost host, IPluginSessionContext session, YouTrackBackend backend, YouTrackSettings settings) =>
        // One picker per session pane: a second pick for the same pane should refocus it, not open another.
        _ = host.ShowDialogAsync(
            "Track an issue in this session",
            () => new YouTrackIssuePickerControl(settings, backend, session.PaneId, link => backend.LinkAsync(session.PaneId, link)),
            $"track.{session.PaneId}",
            width: 720,
            height: 520);

    private void _ShowMenu()
    {
        if (_link is not { } link)
        {
            return;
        }

        var menu = new ContextMenu();
        var items = new List<MenuItem>();

        // The board's own order, read from the project: forward is the next column, back is the previous one. A
        // state-machine board answers for itself and has neither — its events *are* the moves.
        if (_fields?.State is { } state)
        {
            if (StateFlow.Forward(state) is { } forward)
            {
                var item = new MenuItem { Header = $"Move forward → {forward}" };
                item.Click += async (_, _) => await _SetStateAsync(link, forward);
                items.Add(item);
            }

            if (StateFlow.Back(state) is { } back)
            {
                var item = new MenuItem { Header = $"Move back ← {back}" };
                item.Click += async (_, _) => await _SetStateAsync(link, back);
                items.Add(item);
            }

            // Everything else the board allows. Hidden a level down rather than left out: YouTrack lets you jump, so
            // a menu that pretended otherwise would be lying about what you can do — but the two moves above are the
            // ones you want nine times in ten.
            var elsewhere = StateFlow.Elsewhere(state);
            if (elsewhere.Count > 0)
            {
                var others = new MenuItem { Header = items.Count > 0 ? "Move somewhere else" : "Move to" };
                var targets = new List<MenuItem>();

                foreach (var target in elsewhere)
                {
                    var item = new MenuItem { Header = target };
                    item.Click += async (_, _) => await _SetStateAsync(link, target);
                    targets.Add(item);
                }

                others.ItemsSource = targets;
                items.Add(others);
            }
        }

        if (items.Count > 0)
        {
            items.Add(new MenuItem { Header = "-" });
        }

        var branch = new MenuItem { Header = "Copy branch name" };
        branch.Click += async (_, _) => await _CopyBranchNameAsync(link);
        items.Add(branch);

        var open = new MenuItem { Header = "Open in browser" };
        open.Click += (_, _) => _OpenInBrowser(link);
        items.Add(open);

        var unlink = new MenuItem { Header = "Unlink from this session" };
        unlink.Click += async (_, _) => await _UnlinkAsync(link);
        items.Add(unlink);

        menu.ItemsSource = items;
        menu.PlacementTarget = _row;
        menu.Open(_row);
    }

    private async Task _SetStateAsync(LinkedIssue link, string target)
    {
        if (_fields?.State is not { } state)
        {
            return;
        }

        try
        {
            await _backend.SetStateAsync(link.Instance, link.Issue, state, target, _session.PaneId);
            await _LoadAsync();
        }
        catch (Exception exception)
        {
            _host.ShowToast($"{link.Issue.IdReadable}: {exception.Message}", PluginToastSeverity.Error);
        }
    }

    private async Task _UnlinkAsync(LinkedIssue link)
    {
        try
        {
            await _backend.UnlinkAsync(_session.PaneId);
        }
        catch (Exception exception)
        {
            _host.ShowToast($"{link.Issue.IdReadable}: {exception.Message}", PluginToastSeverity.Error);
        }
    }

    private async Task _CopyBranchNameAsync(LinkedIssue link)
    {
        var name = BranchName.From(link.Issue.IdReadable, link.Issue.Summary, _settings.BranchPattern);
        await _host.SetClipboardTextAsync(name);
        _host.ShowToast($"Branch name copied: {name}", PluginToastSeverity.Success);
    }

    private void _OpenInBrowser(LinkedIssue link)
    {
        var url = YouTrackUrl.BuildIssueUrl(link.Instance.InstanceUrl, link.Issue.IdReadable);
        if (YouTrackBrowser.Open(url) is { } failure)
        {
            _host.ShowToast(failure, PluginToastSeverity.Error);
        }
    }
}
