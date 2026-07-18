using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GitStatus;

/// <summary>
/// The git state of the repo a session is working in, shown in that session's own header bar: a coloured dot
/// (clean / has changes / not a repo) plus the branch, with the counts on hover. It belongs here rather than in
/// the sidebar because it describes one session, and the sidebar describes the cockpit — a section following
/// "whichever session is selected" says nothing about the other three panes on screen.
/// Refreshes when the session's working directory becomes known and when the session runs a git command; click
/// to drop the status summary into that session.
/// </summary>
internal sealed class GitStatusHeaderControl : UserControl
{
    // A git command may print progress over several lines; coalesce the burst and let the working tree settle
    // before re-reading.
    private static readonly TimeSpan SignalDebounce = TimeSpan.FromSeconds(2);

    private readonly ICockpitHost _host;
    private readonly IPluginSessionContext _session;
    private readonly GitStatusSettings _settings;
    private readonly GitStatusReader _reader = new();
    private readonly DispatcherTimer _signalRefresh;

    private readonly Ellipse _dot;
    private readonly TextBlock _label;
    private readonly Button _row;

    private GitRepoStatus? _current;
    private int _loadToken;

    private FileSystemWatcher? _headWatcher;
    private string? _watchedHeadDirectory;

    public GitStatusHeaderControl(ICockpitHost host, IPluginSessionContext session, GitStatusSettings settings)
    {
        _host = host;
        _session = session;
        _settings = settings;

        _dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
        _label = new TextBlock { FontSize = 10, VerticalAlignment = VerticalAlignment.Center };

        // Toggling "show branch name" in settings takes effect at once, not on the next git signal — the callback
        // runs on the UI thread (AC-36). Only the label's visibility depends on the setting, so update it directly.
        _host.OnSettingsSaved(() => _label.IsVisible = _settings.ShowBranchName);

        _row = new Button
        {
            Padding = new Thickness(6, 1),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children = { _dot, _label },
            },
        };
        _row.Click += async (_, _) => await _InjectAsync();

        Content = _row;
        IsVisible = false;

        _signalRefresh = new DispatcherTimer { Interval = SignalDebounce };
        _signalRefresh.Tick += (_, _) =>
        {
            _signalRefresh.Stop();
            _ = _LoadAsync();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _session.WorkingDirectoryChanged += _OnWorkingDirectoryChanged;
        _session.OutputProduced += _OnSessionOutput;
        _ = _LoadAsync();
        _ = _EnsureHeadWatcherAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _session.WorkingDirectoryChanged -= _OnWorkingDirectoryChanged;
        _session.OutputProduced -= _OnSessionOutput;
        _signalRefresh.Stop();
        _DisposeHeadWatcher();
    }

    private void _OnWorkingDirectoryChanged(object? sender, EventArgs e)
    {
        _ = _LoadAsync();
        _ = _EnsureHeadWatcherAsync();
    }

    private void _OnSessionOutput(object? sender, SessionOutputText output)
    {
        // Only a git-mutating command is worth a re-read; ordinary prose about git is not.
        if (GitSignalDetector.ContainsSignal(output.Text))
        {
            _ScheduleReload();
        }
    }

    // A checkout writes .git/HEAD, which the session produces no output about when it happens in another terminal
    // or the IDE — so watch HEAD directly and let the badge follow it. git updates HEAD atomically (write
    // HEAD.lock, rename onto HEAD), hence the rename subscription alongside the write one.
    private async Task _EnsureHeadWatcherAsync()
    {
        var path = _session.WorkingDirectory;
        var headFile = string.IsNullOrWhiteSpace(path)
            ? null
            : await GitHeadLocator.ResolveHeadFileAsync(path, CancellationToken.None);
        var directory = headFile is null ? null : System.IO.Path.GetDirectoryName(headFile);

        if (directory is null)
        {
            _DisposeHeadWatcher();
            return;
        }

        // The working directory changed but still resolves to the same repo — keep the watcher we have.
        if (_headWatcher is not null && string.Equals(directory, _watchedHeadDirectory, StringComparison.Ordinal))
        {
            return;
        }

        _DisposeHeadWatcher();
        try
        {
            var watcher = new FileSystemWatcher(directory, "HEAD")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            };
            watcher.Changed += _OnHeadChanged;
            watcher.Created += _OnHeadChanged;
            watcher.Renamed += _OnHeadChanged;
            watcher.EnableRaisingEvents = true;
            _headWatcher = watcher;
            _watchedHeadDirectory = directory;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // The OS can refuse the watch (an inotify limit, a directory that just vanished): the badge keeps its
            // other refresh triggers, only following an external checkout is lost.
            _watchedHeadDirectory = null;
        }
    }

    private void _OnHeadChanged(object? sender, FileSystemEventArgs e) => Dispatcher.UIThread.Post(_ScheduleReload);

    private void _ScheduleReload()
    {
        _signalRefresh.Stop();
        _signalRefresh.Start();
    }

    private void _DisposeHeadWatcher()
    {
        if (_headWatcher is null)
        {
            return;
        }

        _headWatcher.EnableRaisingEvents = false;
        _headWatcher.Changed -= _OnHeadChanged;
        _headWatcher.Created -= _OnHeadChanged;
        _headWatcher.Renamed -= _OnHeadChanged;
        _headWatcher.Dispose();
        _headWatcher = null;
        _watchedHeadDirectory = null;
    }

    private async Task _LoadAsync()
    {
        var path = _session.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(path))
        {
            _current = null;
            IsVisible = false;
            return;
        }

        // Guard against overlapping loads (a signal burst, or the directory arriving mid-read): only the latest wins.
        var token = ++_loadToken;
        try
        {
            var status = await _reader.ReadAsync(path, CancellationToken.None);
            if (token != _loadToken)
            {
                return;
            }

            _current = status;
            _Render(status);
        }
        catch (Exception exception)
        {
            if (token == _loadToken)
            {
                _current = null;
                _Render(exception.Message);
            }
        }
    }

    private void _Render(GitRepoStatus status)
    {
        // A session working somewhere that is not a repository has nothing to say here, so the indicator stays
        // out of the header entirely rather than sitting there greyed out.
        if (status.Error is not null)
        {
            IsVisible = false;
            return;
        }

        IsVisible = true;
        _dot.Fill = status.IsClean ? _Brush("CockpitStatusDoneBrush") : _Brush("CockpitStatusWaitingBrush");
        _label.Text = status.Branch;
        // The dot is always the at-a-glance status; the branch name is optional (AC-36). Hidden, it lives on in the
        // tooltip below, so no information is lost — only header width.
        _label.IsVisible = _settings.ShowBranchName;
        ToolTip.SetTip(_row, $"{status.Name} · {status.Branch}\n{GitStatusSummary.Describe(status)}\n\nClick to add this summary to the session's prompt.");
    }

    private void _Render(string error)
    {
        IsVisible = false;
        ToolTip.SetTip(_row, $"Could not read git status: {error}");
    }

    private async Task _InjectAsync()
    {
        if (_current is not { Error: null } status)
        {
            return;
        }

        var summary = $"Current git status of {status.Name} ({status.Path}) on '{status.Branch}': {GitStatusSummary.Describe(status)}";
        if (_host.Actions.HasActiveSession)
        {
            await _host.Actions.InjectIntoActiveSessionAsync(summary);
        }
        else
        {
            await _host.Actions.SetClipboardTextAsync(summary);
        }
    }

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
