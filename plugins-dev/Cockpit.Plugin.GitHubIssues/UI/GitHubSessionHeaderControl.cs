using Avalonia;
using Avalonia.Controls;
using System.Text.Json;
using Avalonia.Layout;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;
using Cockpit.Plugin.GitHubIssues.Contracts;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.GitHubIssues.UI;

// The issue this session is working on, in its own header (#77) — and, before you have picked one, the way to pick
// it. The same shape as the YouTrack badge, because it answers the same question and there is no reason for it to
// look like a different app.
//
// The actions on it are the ones GitHub actually has. There is no status to set — an issue is open or closed — so
// the menu offers what people do instead: assign it to yourself, put on the label your repo uses for work in flight,
// comment, close.
//
// AC-1396: the link lives in the backend part now. This control asks for its pane's issue when it is attached, and
// otherwise follows the backend's LinkChanged event; the GitHub actions are channel calls too.
internal sealed class GitHubSessionHeaderControl : UserControl
{
    private readonly ICockpitUiHost _host;
    private readonly IPluginSessionContext _session;
    private readonly GitHubIssuesSettings _settings;

    private readonly TextBlock _label;
    private readonly Button _row;

    private GitHubIssue? _issue;
    private IDisposable? _linkChanged;
    private int _loadToken;

    public GitHubSessionHeaderControl(ICockpitUiHost host, IPluginSessionContext session, GitHubIssuesSettings settings)
    {
        _host = host;
        _session = session;
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
                    new MaterialIcon { Kind = MaterialIconKind.Github, Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center },
                    _label,
                },
            },
        };

        // The badge says what this session is working on, and says nothing when it is not working on an issue: picking
        // one is an action, and actions live in the header's own menu.
        _row.Click += (_, _) =>
        {
            if (_issue is { } issue)
            {
                _ShowMenu(issue);
            }
        };

        Content = _row;
        _Render();

        // The pane may have been linked before this control existed (a session started from an issue), so the
        // current link is asked for on attach rather than only heard about from the next change.
        AttachedToVisualTree += async (_, _) => await _LoadAsync();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _linkChanged = _host.Channel.Subscribe(GitHubIssuesChannel.LinkChanged, _OnLinkChanged);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _linkChanged?.Dispose();
        _linkChanged = null;
    }

    // Raised on the backend's publishing thread, not the UI thread — marshal before touching a control.
    private void _OnLinkChanged(PluginChannelEvent channelEvent)
    {
        var changed = channelEvent.Payload.Deserialize<GitHubIssuesLinkChanged>(GitHubIssuesChannel.Json);
        if (changed is null || !string.Equals(changed.PaneId, _session.PaneId, StringComparison.Ordinal))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            // A change outranks a load still in flight: that load would answer with the link as it was.
            ++_loadToken;
            _issue = changed.Issue;
            _Render();
        });
    }

    private async Task _LoadAsync()
    {
        var token = ++_loadToken;
        try
        {
            var issue = await _host.Channel.AskAsync<GitHubIssue>(GitHubIssuesChannel.LinkedIssue, new GitHubIssuesPaneRequest(_session.PaneId));
            if (token == _loadToken)
            {
                _issue = issue;
                _Render();
            }
        }
        catch (Exception)
        {
            // Best-effort: the badge stays as it was, and the next LinkChanged brings it up to date.
        }
    }

    private void _Render()
    {
        if (_issue is not { } issue)
        {
            IsVisible = false;
            return;
        }

        IsVisible = true;
        _label.Text = $"#{issue.Number} · {issue.Repository}";
        ToolTip.SetTip(_row, $"{issue.Title}\n{issue.Repository}#{issue.Number}\n\nClick for actions.");
    }

    // Opens the picker for one pane — what the header menu's "Track a GitHub issue" runs.
    // A menu action cannot await, so the picker is awaited here and a failure to open it is told rather than lost.
    public static async void Pick(ICockpitUiHost host, IPluginSessionContext session)
    {
        try
        {
            // One picker per session pane: a second pick for the same pane should refocus it, not open another.
            await host.ShowDialogAsync(
                "Track an issue in this session",
                () => new GitHubIssuePickerControl(
                    host,
                    session.PaneId,
                    issue => host.Channel.AskAsync<object>(
                        GitHubIssuesChannel.Link,
                        new GitHubIssuesLinkRequest(session.PaneId, issue, session.WorkingDirectory))),
                $"track.{session.PaneId}",
                width: 720,
                height: 520);
        }
        catch (Exception exception)
        {
            host.ShowToast($"Could not open the issue picker: {exception.Message}", PluginToastSeverity.Error);
        }
    }

    private void _ShowMenu(GitHubIssue issue)
    {
        var reference = new GitHubIssueRequest(issue.Repository, issue.Number);
        var items = new List<MenuItem>();

        var assign = new MenuItem { Header = "Assign to me" };
        assign.Click += async (_, _) => await _DoAsync(
            () => _host.Channel.AskAsync<object>(GitHubIssuesChannel.AssignToMe, reference),
            $"#{issue.Number} assigned to you.");
        items.Add(assign);

        // The label a repo uses for work in flight, if the operator named one in settings. GitHub enforces no
        // convention, so an empty setting means the menu simply does not offer it — rather than offering a label that
        // does not exist and failing on the click.
        if (_settings.InProgressLabel is { Length: > 0 } label)
        {
            var mark = new MenuItem { Header = $"Label '{label}'" };
            mark.Click += async (_, _) => await _DoAsync(
                () => _host.Channel.AskAsync<object>(GitHubIssuesChannel.AddLabel, new GitHubIssueLabelRequest(issue.Repository, issue.Number, label)),
                $"#{issue.Number} labelled '{label}'.");
            items.Add(mark);
        }

        items.Add(new MenuItem { Header = "-" });

        var complete = new MenuItem { Header = "Close as completed" };
        complete.Click += async (_, _) => await _DoAsync(
            () => _host.Channel.AskAsync<object>(GitHubIssuesChannel.Close, new GitHubIssueCloseRequest(issue.Repository, issue.Number, "completed")),
            $"#{issue.Number} closed as completed.");
        items.Add(complete);

        var notPlanned = new MenuItem { Header = "Close as not planned" };
        notPlanned.Click += async (_, _) => await _DoAsync(
            () => _host.Channel.AskAsync<object>(GitHubIssuesChannel.Close, new GitHubIssueCloseRequest(issue.Repository, issue.Number, "not planned")),
            $"#{issue.Number} closed as not planned.");
        items.Add(notPlanned);

        items.Add(new MenuItem { Header = "-" });

        var branch = new MenuItem { Header = "Copy branch name" };
        branch.Click += async (_, _) =>
        {
            var name = GitHubBranchName.From(issue.Number, issue.Title, _settings.BranchPattern);
            await _host.SetClipboardTextAsync(name);
            _host.ShowToast($"Branch name copied: {name}", PluginToastSeverity.Success);
        };
        items.Add(branch);

        var open = new MenuItem { Header = "Open in browser" };
        open.Click += (_, _) =>
        {
            if (GitHubBrowser.Open(issue.Url) is { } failure)
            {
                _host.ShowToast(failure, PluginToastSeverity.Error);
            }
        };
        items.Add(open);

        var unlink = new MenuItem { Header = "Stop tracking it here" };
        unlink.Click += async (_, _) => await _UnlinkAsync();
        items.Add(unlink);

        var menu = new ContextMenu { ItemsSource = items, PlacementTarget = _row };
        menu.Open(_row);
    }

    private async Task _UnlinkAsync()
    {
        try
        {
            await _host.Channel.AskAsync<object>(GitHubIssuesChannel.Unlink, new GitHubIssuesPaneRequest(_session.PaneId));
        }
        catch (Exception exception)
        {
            _host.ShowToast(exception.Message, PluginToastSeverity.Error);
        }
    }

    // Every action here changes something on GitHub, so a failure is said out loud rather than swallowed: an issue
    // the operator believes is closed and is not is worse than an error message.
    private async Task _DoAsync(Func<Task<object?>> action, string said)
    {
        try
        {
            await action();
            _host.ShowToast(said, PluginToastSeverity.Success);
        }
        catch (Exception exception)
        {
            _host.ShowToast(exception.Message, PluginToastSeverity.Error);
        }
    }
}
