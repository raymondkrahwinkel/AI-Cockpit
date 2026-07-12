using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitStatus;

/// <summary>
/// The "Git status" dialog opened from the left-menu button (#1): a sortable <see cref="DataGrid"/> of the
/// configured repositories' status (state, repository, branch, uncommitted change count, ahead/behind, path),
/// a Refresh, and Inject/Copy of a multi-repo status summary. "Inject status into session" drops the summary
/// into the active session (clipboard fallback when none is active). Built in code; the DataGrid theme is
/// provided app-wide by the host.
/// </summary>
internal sealed class GitStatusDialogControl : UserControl
{
    private readonly GitStatusSettings _settings;
    private readonly ICockpitActions _actions;
    private readonly GitStatusReader _reader = new();

    private readonly DataGrid _grid;
    private readonly TextBlock _status;
    private readonly Button _inject;
    private readonly ObservableCollection<GitRepoStatus> _rows = [];

    public GitStatusDialogControl(GitStatusSettings settings, ICockpitActions actions)
    {
        _settings = settings;
        _actions = actions;

        var refresh = new Button { Content = "↻ Refresh" };
        refresh.Click += async (_, _) => await _LoadAsync();

        _inject = new Button { Content = "Inject status into session", Classes = { "Accent" } };
        _inject.Click += async (_, _) => await _InjectAsync();

        var copy = new Button { Content = "⧉ Copy" };
        copy.Click += async (_, _) => await _CopyAsync();

        _status = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

        _grid = new DataGrid
        {
            ItemsSource = _rows,
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserSortColumns = true,
            SelectionMode = DataGridSelectionMode.Single,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        };
        _grid.Columns.Add(new DataGridTextColumn { Header = "State", Binding = new Binding(nameof(GitRepoStatus.StateText)), Width = new DataGridLength(90) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Repository", Binding = new Binding(nameof(GitRepoStatus.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Branch", Binding = new Binding(nameof(GitRepoStatus.Branch)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Changes", Binding = new Binding(nameof(GitRepoStatus.Uncommitted)), Width = new DataGridLength(80) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Remote", Binding = new Binding(nameof(GitRepoStatus.RemoteText)), Width = new DataGridLength(120) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Path", Binding = new Binding(nameof(GitRepoStatus.Path)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _grid.SelectionChanged += (_, _) => _ShowSelectedDetail();

        var topBar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(refresh, Dock.Left);
        var actionsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        actionsRow.Children.Add(copy);
        actionsRow.Children.Add(_inject);
        topBar.Children.Add(refresh);
        topBar.Children.Add(actionsRow);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(topBar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(topBar);
        root.Children.Add(_status);
        root.Children.Add(_grid);
        Content = root;

        _ = _LoadAsync();
    }

    private async Task _LoadAsync()
    {
        var repos = _settings.Repos;
        if (repos.Count == 0)
        {
            _rows.Clear();
            _inject.IsEnabled = false;
            _status.Text = "No repositories configured — add some via the ⚙ Settings gear next to this plugin (in the Plugin store → Installed).";
            return;
        }

        _status.Text = "Reading git status…";
        var results = new List<GitRepoStatus>();
        foreach (var path in repos)
        {
            results.Add(await _reader.ReadAsync(path, CancellationToken.None));
        }

        _rows.Clear();
        foreach (var result in results)
        {
            _rows.Add(result);
        }

        var errors = results.Count(status => status.Error is not null);
        var dirty = results.Count(status => status.Error is null && !status.IsClean);
        var clean = results.Count - dirty - errors;
        _inject.IsEnabled = results.Count > 0;
        _status.Text = $"{results.Count} repo(s): {dirty} with changes/unpushed, {clean} clean"
            + (errors > 0 ? $", {errors} error(s)" : string.Empty)
            + ". Click a row for detail.";
    }

    private void _ShowSelectedDetail()
    {
        if (_grid.SelectedItem is GitRepoStatus status)
        {
            _status.Text = $"{status.Name} ({status.Path}) on '{status.Branch}': {GitStatusSummary.Describe(status)}";
        }
    }

    private async Task _InjectAsync()
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var text = GitStatusSummary.Render([.. _rows]);
        if (_actions.HasActiveSession)
        {
            await _actions.InjectIntoActiveSessionAsync(text);
            _status.Text = "Inserted the git status summary into the active session.";
        }
        else
        {
            await _actions.SetClipboardTextAsync(text);
            _status.Text = "No active session — copied the summary to the clipboard instead.";
        }
    }

    private async Task _CopyAsync()
    {
        if (_rows.Count == 0)
        {
            return;
        }

        await _actions.SetClipboardTextAsync(GitStatusSummary.Render([.. _rows]));
        _status.Text = "Copied the git status summary to the clipboard.";
    }
}
