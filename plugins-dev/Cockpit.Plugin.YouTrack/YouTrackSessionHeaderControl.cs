using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// The issue a session is working on, in that session's own header bar (#75): its id and status, and a menu with
/// the moves the board actually allows. Which session matters here — the cockpit shows several at once, and the
/// ticket you are working on in one pane says nothing about the other three — so this is bound to its own pane
/// via <see cref="IPluginSessionContext.PaneId"/> rather than following the selection.
/// Shows nothing at all until an issue is linked: an empty indicator in every header is noise.
/// </summary>
internal sealed class YouTrackSessionHeaderControl : UserControl
{
    private readonly ICockpitHost _host;
    private readonly IPluginSessionContext _session;
    private readonly SessionIssueLinks _links;
    private readonly YouTrackSettings _settings;
    private readonly YouTrackClient _client = new();

    private readonly TextBlock _label;
    private readonly Button _row;

    private YouTrackIssueFields? _fields;
    private int _loadToken;

    public YouTrackSessionHeaderControl(ICockpitHost host, IPluginSessionContext session, SessionIssueLinks links, YouTrackSettings settings)
    {
        _host = host;
        _session = session;
        _links = links;
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
                    new TextBlock { Text = "🎫", FontSize = 10, VerticalAlignment = VerticalAlignment.Center },
                    _label,
                },
            },
        };
        _row.Click += (_, _) =>
        {
            // With an issue: the actions on it. Without: the question that comes first — which ticket is this session
            // for. The header is where you are looking when you ask it, so it is where you answer it.
            if (_links.For(_session.PaneId) is null)
            {
                _Pick();
            }
            else
            {
                _ShowMenu();
            }
        };

        Content = _row;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _links.Changed += _OnLinkChanged;
        _ = _LoadAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _links.Changed -= _OnLinkChanged;
    }

    private void _OnLinkChanged(object? sender, string paneId)
    {
        if (string.Equals(paneId, _session.PaneId, StringComparison.Ordinal))
        {
            _ = _LoadAsync();
        }
    }

    // Re-reads the linked issue's status from YouTrack: what it is worth now, and what it may become. Only the
    // latest load wins — clicking through the menu can start a second one before the first returns.
    private async Task _LoadAsync()
    {
        if (_links.For(_session.PaneId) is not { } link)
        {
            // No ticket yet: the badge is the way to pick one, not a thing that disappears until you have picked one
            // somewhere else.
            _fields = null;
            _label.Text = "Track an issue";
            _label.Opacity = 0.6;
            ToolTip.SetTip(_row, "Pick the YouTrack issue this session is for.");
            return;
        }

        _label.Opacity = 1;

        var token = ++_loadToken;
        _label.Text = link.Issue.IdReadable;
        IsVisible = true;

        try
        {
            var fields = await _client.GetIssueFieldsAsync(link.Instance.InstanceUrl, link.Instance.Token, link.Issue, CancellationToken.None);
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

    // Opens the picker for *this* pane. Linking from the big dialog links to whichever session is selected, which is
    // a guess as soon as four panes are open; the header knows which one it is.
    private void _Pick() => _ = _host.ShowDialogAsync(
        "Track an issue in this session",
        () => new YouTrackIssuePickerControl(_settings, link =>
        {
            _links.Link(_session.PaneId, link, _session.WorkingDirectory);
            _CloseDialog();
        }),
        720,
        520);

    private void _CloseDialog()
    {
        // The picker owns no window; the host does. Closing it from here means walking up to whatever window the host
        // opened, which is the one thing a plugin control can do about a dialog it did not create.
        if (TopLevel.GetTopLevel(this) is Window window && window.OwnedWindows.Count > 0)
        {
            window.OwnedWindows[^1].Close();
        }
    }

    private void _ShowMenu()
    {
        if (_links.For(_session.PaneId) is not { } link)
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
        unlink.Click += (_, _) => _links.Unlink(_session.PaneId);
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
            await _client.SetStateAsync(link.Instance.InstanceUrl, link.Instance.Token, link.Issue, state, target, CancellationToken.None);
            await _LoadAsync();
        }
        catch (Exception exception)
        {
            _host.ShowToast($"{link.Issue.IdReadable}: {exception.Message}", PluginToastSeverity.Error);
        }
    }

    private async Task _CopyBranchNameAsync(LinkedIssue link)
    {
        var name = BranchName.From(link.Issue.IdReadable, link.Issue.Summary, _settings.BranchPattern);
        await _host.Actions.SetClipboardTextAsync(name);
        _host.ShowToast($"Branch name copied: {name}", PluginToastSeverity.Success);
    }

    private void _OpenInBrowser(LinkedIssue link)
    {
        var url = YouTrackClient.BuildIssueUrl(link.Instance.InstanceUrl, link.Issue.IdReadable);

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _host.ShowToast($"Could not open the browser: {exception.Message}", PluginToastSeverity.Error);
        }
    }
}
