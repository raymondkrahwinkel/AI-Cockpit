using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// Picks an issue for one session (#75). Opened from that session's own header, so the issue lands on the pane you
/// opened it from — not on "the active session", which is a guess the moment four panes are on screen.
/// <para>
/// It is a list of your open issues, and a box to narrow it. Deliberately not the full YouTrack dialog: this is the
/// question "which of my tickets am I working on in this pane", and everything that does not help answer it is in the
/// way.
/// </para>
/// </summary>
internal sealed class YouTrackIssuePickerControl : UserControl
{
    private const int MaxResults = 100;

    private readonly YouTrackSettings _settings;
    private readonly Action<LinkedIssue> _picked;
    private readonly YouTrackClient _client = new();

    private readonly ComboBox _instances;
    private readonly TextBox _search;
    private readonly CheckBox _mine;
    private readonly ListBox _issues;
    private readonly TextBlock _status;

    private List<(YouTrackInstance Instance, YouTrackIssue Issue)> _all = [];

    public YouTrackIssuePickerControl(YouTrackSettings settings, Action<LinkedIssue> picked)
    {
        _settings = settings;
        _picked = picked;

        _status = new TextBlock { FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

        _instances = new ComboBox { MinWidth = 160, ItemsSource = _Configured().Select(instance => instance.Label).ToList() };
        _instances.SelectionChanged += async (_, _) => await _LoadAsync();

        _search = new TextBox { Watermark = "Filter by id or summary…", MinWidth = 220 };
        _search.TextChanged += (_, _) => _Render();

        _mine = new CheckBox { Content = "Assigned to me", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        _mine.IsCheckedChanged += async (_, _) => await _LoadAsync();

        _issues = new ListBox { Margin = new Thickness(0, 8, 0, 0) };
        _issues.DoubleTapped += (_, _) => _Pick();

        var use = new Button { Content = "Track in this session", Classes = { "Accent" } };
        use.Click += (_, _) => _Pick();

        Content = new DockPanel
        {
            Margin = new Thickness(14),
            Children =
            {
                _Docked(
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { _instances, _search, _mine },
                    },
                    Dock.Top),
                _Docked(
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Margin = new Thickness(0, 10, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { use },
                    },
                    Dock.Bottom),
                _Docked(_status, Dock.Bottom),
                _issues,
            },
        };

        if (_instances.ItemCount > 0)
        {
            _instances.SelectedIndex = 0;
        }
        else
        {
            _status.Text = "No YouTrack instance is configured. Open the plugin's settings first.";
        }
    }

    private IReadOnlyList<YouTrackInstance> _Configured() =>
        _settings.Instances.Where(instance => instance.InstanceUrl.Length > 0 && instance.Token.Length > 0).ToList();

    private async Task _LoadAsync()
    {
        if (_Configured().ElementAtOrDefault(_instances.SelectedIndex) is not { } instance)
        {
            return;
        }

        _status.Text = "Looking…";
        _issues.ItemsSource = null;

        try
        {
            // What the operator said they want to see. The client's own query is "#Unresolved"; anything written here
            // replaces the states half of it, so "done" issues stay out unless someone asks for them.
            var issues = await _client.GetOpenIssuesAsync(
                instance.InstanceUrl,
                instance.Token,
                instance.DefaultProjectTag is { Length: > 0 } tag ? tag : null,
                _settings.PickerQuery,
                _mine.IsChecked == true,
                MaxResults,
                CancellationToken.None);

            _all = issues.Select(issue => (instance, issue)).ToList();
            _status.Text = _all.Count == 0 ? "No open issues here." : string.Empty;
            _Render();
        }
        catch (Exception exception)
        {
            _all = [];
            _issues.ItemsSource = null;
            _status.Text = $"Could not reach YouTrack: {exception.Message}";
        }
    }

    private void _Render()
    {
        var term = _search.Text?.Trim();

        var matches = string.IsNullOrEmpty(term)
            ? _all
            : _all.Where(entry =>
                entry.Issue.IdReadable.Contains(term, StringComparison.OrdinalIgnoreCase)
                || entry.Issue.Summary.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();

        _issues.ItemsSource = matches
            .Select(entry => new IssueRow(entry.Instance, entry.Issue))
            .ToList();

        if (_issues.ItemCount > 0)
        {
            _issues.SelectedIndex = 0;
        }
    }

    private void _Pick()
    {
        if (_issues.SelectedItem is not IssueRow row)
        {
            return;
        }

        _picked(new LinkedIssue(row.Instance, row.Issue));
    }

    private static Control _Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    // What a row shows: the id and the summary, which is how a person recognises their own ticket. The state is not
    // here — every open issue in this list is one you could pick up, and a column of "Open / Open / Open" is noise.
    private sealed record IssueRow(YouTrackInstance Instance, YouTrackIssue Issue)
    {
        public override string ToString() => $"{Issue.IdReadable} · {Issue.Summary}";
    }
}
