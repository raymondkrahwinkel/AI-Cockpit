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
        // The badge is what this session has to *say*, so it says nothing when there is no ticket: picking one is an
        // action, and actions live in the header's own menu, where every plugin's fit in the room of one button.
        _row.Click += (_, _) => _ShowMenu();

        Content = _row;
        IsVisible = false;
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
            _fields = null;
            IsVisible = false;
            return;
        }

        IsVisible = true;

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

    /// <summary>Opens the picker for one pane — what the header menu's "Track a YouTrack issue" runs. Linking from the big dialog links to whichever session is selected, which is a guess as soon as four panes are open.</summary>
    public static void Pick(ICockpitHost host, IPluginSessionContext session, SessionIssueLinks links, YouTrackSettings settings) =>
        _ = host.ShowDialogAsync(
            "Track an issue in this session",
            () => new YouTrackIssuePickerControl(settings, link => links.Link(session.PaneId, link, session.WorkingDirectory)),
            720,
            520);

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
