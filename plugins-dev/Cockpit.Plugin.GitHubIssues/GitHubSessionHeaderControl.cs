using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Material.Icons;
using Material.Icons.Avalonia;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// The issue this session is working on, in its own header (#77) — and, before you have picked one, the way to pick
/// it. The same shape as the YouTrack badge, because it answers the same question and there is no reason for it to
/// look like a different app.
/// <para>
/// The actions on it are the ones GitHub actually has. There is no status to set — an issue is open or closed — so
/// the menu offers what people do instead: assign it to yourself, put on the label your repo uses for work in flight,
/// comment, close.
/// </para>
/// </summary>
internal sealed class GitHubSessionHeaderControl : UserControl
{
    private readonly ICockpitHost _host;
    private readonly IPluginSessionContext _session;
    private readonly SessionIssueLinks _links;
    private readonly GitHubIssuesSettings _settings;
    private readonly GitHubWorkflowClient _client = new();

    private readonly TextBlock _label;
    private readonly Button _row;

    public GitHubSessionHeaderControl(ICockpitHost host, IPluginSessionContext session, SessionIssueLinks links, GitHubIssuesSettings settings)
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
                    new MaterialIcon { Kind = MaterialIconKind.Github, Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center },
                    _label,
                },
            },
        };

        // The badge says what this session is working on, and says nothing when it is not working on an issue: picking
        // one is an action, and actions live in the header's own menu.
        _row.Click += (_, _) =>
        {
            if (_links.For(_session.PaneId) is { } issue)
            {
                _ShowMenu(issue);
            }
        };

        Content = _row;
        _Render();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _links.Changed += _OnChanged;
        _Render();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _links.Changed -= _OnChanged;
    }

    private void _OnChanged(object? sender, string paneId)
    {
        if (string.Equals(paneId, _session.PaneId, StringComparison.Ordinal))
        {
            _Render();
        }
    }

    private void _Render()
    {
        if (_links.For(_session.PaneId) is not { } issue)
        {
            IsVisible = false;
            return;
        }

        IsVisible = true;
        _label.Text = $"#{issue.Number} · {issue.Repository}";
        ToolTip.SetTip(_row, $"{issue.Title}\n{issue.Repository}#{issue.Number}\n\nClick for actions.");
    }

    /// <summary>Opens the picker for one pane — what the header menu's "Track a GitHub issue" runs.</summary>
    public static void Pick(ICockpitHost host, IPluginSessionContext session, SessionIssueLinks links, GitHubIssuesSettings settings) =>
        _ = host.ShowDialogAsync(
            "Track an issue in this session",
            () => new GitHubIssuePickerControl(settings, issue => links.Link(session.PaneId, issue, session.WorkingDirectory)),
            720,
            520);

    private void _ShowMenu(GitHubIssue issue)
    {
        var reference = new GitHubIssueReference(issue.Repository, issue.Number);
        var items = new List<MenuItem>();

        var assign = new MenuItem { Header = "Assign to me" };
        assign.Click += async (_, _) => await _DoAsync(
            () => _client.AssignToMeAsync(reference, CancellationToken.None),
            $"#{issue.Number} assigned to you.");
        items.Add(assign);

        // The label a repo uses for work in flight, if the operator named one in settings. GitHub enforces no
        // convention, so an empty setting means the menu simply does not offer it — rather than offering a label that
        // does not exist and failing on the click.
        if (_settings.InProgressLabel is { Length: > 0 } label)
        {
            var mark = new MenuItem { Header = $"Label '{label}'" };
            mark.Click += async (_, _) => await _DoAsync(
                () => _client.AddLabelAsync(reference, label, CancellationToken.None),
                $"#{issue.Number} labelled '{label}'.");
            items.Add(mark);
        }

        items.Add(new MenuItem { Header = "-" });

        var complete = new MenuItem { Header = "Close as completed" };
        complete.Click += async (_, _) => await _DoAsync(
            () => _client.CloseAsync(reference, "completed", string.Empty, CancellationToken.None),
            $"#{issue.Number} closed as completed.");
        items.Add(complete);

        var notPlanned = new MenuItem { Header = "Close as not planned" };
        notPlanned.Click += async (_, _) => await _DoAsync(
            () => _client.CloseAsync(reference, "not planned", string.Empty, CancellationToken.None),
            $"#{issue.Number} closed as not planned.");
        items.Add(notPlanned);

        items.Add(new MenuItem { Header = "-" });

        var branch = new MenuItem { Header = "Copy branch name" };
        branch.Click += async (_, _) =>
        {
            var name = GitHubBranchName.From(issue.Number, issue.Title, _settings.BranchPattern);
            await _host.Actions.SetClipboardTextAsync(name);
            _host.ShowToast($"Branch name copied: {name}", PluginToastSeverity.Success);
        };
        items.Add(branch);

        var open = new MenuItem { Header = "Open in browser" };
        open.Click += (_, _) => GitHubBrowser.Open(issue.Url);
        items.Add(open);

        var unlink = new MenuItem { Header = "Stop tracking it here" };
        unlink.Click += (_, _) => _links.Unlink(_session.PaneId);
        items.Add(unlink);

        var menu = new ContextMenu { ItemsSource = items, PlacementTarget = _row };
        menu.Open(_row);
    }

    // Every action here changes something on GitHub, so a failure is said out loud rather than swallowed: an issue
    // the operator believes is closed and is not is worse than an error message.
    private async Task _DoAsync(Func<Task> action, string said)
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
