using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Material.Icons.Avalonia;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GitHubActions;

// The GitHub Actions status of the branch a session is working in, in that session's header (AC-52): a coloured icon
// for the latest workflow run on the current branch — green pass, red fail, amber running — with the run's details on
// hover, click to open it on GitHub. Mirrors the git-status header's per-session lifecycle: it re-reads when the
// session's working directory becomes known and on a modest timer (a run's state changes on GitHub, not locally), and
// stays out of the header entirely when there is no repo, no run, or no gh.
internal sealed class CiStatusHeaderControl : UserControl
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly IPluginSessionContext _session;
    private readonly CiWorkflowRunClient _client = new();
    private readonly DispatcherTimer _refresh;
    private readonly MaterialIcon _icon;
    private readonly Button _row;

    private CiRun? _current;
    private int _loadToken;
    private CancellationTokenSource? _loadCts;

    public CiStatusHeaderControl(IPluginSessionContext session)
    {
        _session = session;

        _icon = new MaterialIcon { Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center };
        _row = new Button
        {
            Padding = new Thickness(6, 1),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children = { _icon },
            },
        };
        _row.Click += (_, _) => _OpenRun();

        Content = _row;
        IsVisible = false;

        _refresh = new DispatcherTimer { Interval = RefreshInterval };
        _refresh.Tick += (_, _) => _ = _LoadAsync();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _loadCts = new CancellationTokenSource();
        _session.WorkingDirectoryChanged += _OnWorkingDirectoryChanged;
        _refresh.Start();
        _ = _LoadAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _session.WorkingDirectoryChanged -= _OnWorkingDirectoryChanged;
        _refresh.Stop();
        // Cancel any in-flight gh call so a hung network request does not outlive the closed panel.
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    private void _OnWorkingDirectoryChanged(object? sender, EventArgs e) => _ = _LoadAsync();

    private async Task _LoadAsync()
    {
        var directory = _session.WorkingDirectory;
        if (string.IsNullOrEmpty(directory) || _loadCts is not { } cts)
        {
            _current = null;
            IsVisible = false;
            return;
        }

        var token = ++_loadToken;
        CiRun? run;
        try
        {
            run = await _client.GetLatestRunAsync(directory, cts.Token);
        }
        catch (Exception)
        {
            run = null;
        }

        if (token != _loadToken)
        {
            return; // a newer load superseded this one
        }

        _current = run;
        if (run is null)
        {
            IsVisible = false;
            return;
        }

        IsVisible = true;
        (_icon.Kind, var brush) = CiRunPresentation.Appearance(run.State);
        _icon.Foreground = brush;
        ToolTip.SetTip(_row, _Describe(run));
    }

    private static string _Describe(CiRun run)
    {
        var state = run.State switch
        {
            CiRunState.Passed => "passed",
            CiRunState.Failed => "failed",
            CiRunState.Running => "running",
            _ => string.IsNullOrEmpty(run.Conclusion) ? "unknown" : run.Conclusion,
        };
        var when = run.CreatedAt is { } at ? $" · {CiRunPresentation.Ago(at)}" : string.Empty;
        var workflow = string.IsNullOrEmpty(run.WorkflowName) ? "workflow" : run.WorkflowName;
        return $"CI: {workflow} on '{run.Branch}' — {state} ({run.Event}){when}\n\nClick to open the run on GitHub.";
    }

    private void _OpenRun() => CiWorkflowRunClient.OpenRunInBrowser(_current?.Url);
}
