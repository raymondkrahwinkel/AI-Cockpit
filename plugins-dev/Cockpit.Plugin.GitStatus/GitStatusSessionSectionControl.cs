using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GitStatus;

/// <summary>
/// An inline left-menu section (#1 + read/observe surface) that <em>follows the active session</em>: it reads
/// the git status of the directory the selected session is working in and refreshes when the session switches,
/// when its working directory becomes known, or when the session runs a git command in its output. This is the
/// "couple git status to where the session is busy" ask — the configured-repos button/dialog stays for the
/// at-a-glance overview across many repos, while this shows exactly the one repo in view. Click the row to drop
/// its status summary into the session.
/// </summary>
internal sealed class GitStatusSessionSectionControl : UserControl
{
    // A git command in the output may print progress over several lines; coalesce the burst and let the working
    // tree settle before re-running git status.
    private static readonly TimeSpan SignalDebounce = TimeSpan.FromSeconds(2);

    private readonly ICockpitHost _host;
    private readonly GitStatusReader _reader = new();
    private readonly DispatcherTimer _signalRefresh;

    private readonly TextBlock _title;
    private readonly TextBlock _detail;
    private readonly Button _row;

    private string? _currentPath;
    private GitRepoStatus? _current;
    private int _loadToken;

    public GitStatusSessionSectionControl(ICockpitHost host)
    {
        _host = host;

        _title = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        _detail = new TextBlock { FontSize = 10, Opacity = 0.6, TextWrapping = TextWrapping.Wrap };

        _row = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 4),
            Content = new StackPanel { Children = { _title, _detail } },
        };
        _row.Click += async (_, _) => await _InjectAsync();

        Content = new StackPanel { Margin = new Thickness(4), Spacing = 6, Children = { _row } };

        _signalRefresh = new DispatcherTimer { Interval = SignalDebounce };
        _signalRefresh.Tick += (_, _) =>
        {
            _signalRefresh.Stop();
            _ = _LoadAsync();
        };

        _ShowMessage("Loading…");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _host.Sessions.ActiveSessionChanged += _OnActiveSessionChanged;
        _host.Sessions.OutputProduced += _OnSessionOutput;
        _ = _LoadAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _host.Sessions.ActiveSessionChanged -= _OnActiveSessionChanged;
        _host.Sessions.OutputProduced -= _OnSessionOutput;
        _signalRefresh.Stop();
    }

    private void _OnActiveSessionChanged(object? sender, EventArgs e) => _ = _LoadAsync();

    private void _OnSessionOutput(object? sender, SessionOutputText output)
    {
        // Only the session in view drives this section, and only a git-mutating command is worth a refresh.
        if (output.IsFromActiveSession && GitSignalDetector.ContainsSignal(output.Text))
        {
            _signalRefresh.Stop();
            _signalRefresh.Start();
        }
    }

    private async Task _LoadAsync()
    {
        var path = _host.Sessions.ActiveSessionWorkingDirectory;
        if (string.IsNullOrWhiteSpace(path))
        {
            _currentPath = null;
            _current = null;
            _ShowMessage("No active session.");
            return;
        }

        _currentPath = path;
        // Guard against overlapping loads (fast session switches / signal bursts): only the latest wins.
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
                _ShowMessage($"Could not read git status: {exception.Message}");
            }
        }
    }

    private void _Render(GitRepoStatus status)
    {
        _title.Text = status.Error is not null
            ? $"{status.Name}"
            : $"{status.Name} · {status.Branch}";

        _detail.Text = status.Error is not null
            ? "Not a git repository (or git unavailable)."
            : GitStatusSummary.Describe(status);
    }

    private void _ShowMessage(string message)
    {
        _title.Text = "Session repository";
        _detail.Text = message;
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
}
