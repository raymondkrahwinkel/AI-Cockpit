using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// The inline accordion section registered via <see cref="ICockpitHost.AddSideMenuSection"/>, always visible
/// under the session list: up to <see cref="MaxItems"/> open issues for the configured project, each a
/// clickable row that renders the prompt template and injects it into the active session — or, with no
/// active session, copies it to the clipboard instead — plus a "View all issues" button that opens the full
/// <see cref="YouTrackDialogControl"/> dialog.
/// </summary>
internal sealed class YouTrackSideSectionControl : UserControl
{
    private const int MaxItems = 5;

    private readonly YouTrackSettings _settings;
    private readonly ICockpitHost _host;
    private readonly YouTrackClient _client = new();

    private readonly TextBlock _status;
    private readonly StackPanel _list;

    public YouTrackSideSectionControl(YouTrackSettings settings, ICockpitHost host)
    {
        _settings = settings;
        _host = host;

        _status = new TextBlock { FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _list = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 4) };

        var refresh = new Button { Content = "⟳", FontSize = 11, Padding = new Thickness(6, 2) };
        refresh.Click += async (_, _) => await _LoadAsync();

        var header = new DockPanel();
        DockPanel.SetDock(refresh, Dock.Right);
        header.Children.Add(refresh);
        header.Children.Add(_status);

        var viewAll = new Button
        {
            Content = "View all issues",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Classes = { "Accent" },
        };
        viewAll.Click += (_, _) => _ = _host.ShowDialogAsync(
            "YouTrack Issues",
            () => new YouTrackDialogControl(_settings, _host.Actions),
            1040,
            700);

        Content = new StackPanel
        {
            Margin = new Thickness(4),
            Spacing = 6,
            Children = { header, _list, viewAll },
        };

        // Re-fetch with the just-saved settings (instance URL/token/project) instead of leaving this
        // already-built section showing data loaded under the old configuration until an app restart (#52).
        host.OnSettingsSaved(() => _ = _LoadAsync());

        _ = _LoadAsync();
    }

    private async Task _LoadAsync()
    {
        _status.Text = "Loading…";
        _list.Children.Clear();
        try
        {
            if (string.IsNullOrWhiteSpace(_settings.InstanceUrl) || string.IsNullOrWhiteSpace(_settings.Token) || string.IsNullOrWhiteSpace(_settings.ProjectTag))
            {
                _status.Text = "Set the instance URL, token and project in settings.";
                return;
            }

            var all = await _client.GetOpenIssuesAsync(_settings.InstanceUrl, _settings.Token, _settings.ProjectTag, _settings.ExtraQuery, MaxItems, CancellationToken.None);

            foreach (var issue in all)
            {
                _list.Children.Add(_BuildRow(issue));
            }

            _status.Text = all.Count switch
            {
                0 => "No open issues.",
                _ => $"{all.Count} open issue(s) — click to add to the prompt.",
            };
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not load issues: {exception.Message}";
        }
    }

    private Button _BuildRow(YouTrackIssue issue)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 4),
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = $"{issue.IdReadable} {issue.Summary}", FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = issue.State ?? issue.Project, FontSize = 10, Opacity = 0.6 },
                },
            },
        };
        button.Click += async (_, _) => await _InjectAsync(issue);
        return button;
    }

    private async Task _InjectAsync(YouTrackIssue issue)
    {
        var url = YouTrackClient.BuildIssueUrl(_settings.InstanceUrl, issue.IdReadable);
        var prompt = PromptTemplate.Render(_settings.Template, issue, url);

        if (_host.Actions.HasActiveSession)
        {
            await _host.Actions.InjectIntoActiveSessionAsync(prompt);
            _status.Text = $"✓ Added issue {issue.IdReadable} to the active session's prompt.";
        }
        else
        {
            await _host.Actions.SetClipboardTextAsync(prompt);
            _status.Text = $"✓ No active session — copied issue {issue.IdReadable}'s prompt to the clipboard.";
        }
    }
}
