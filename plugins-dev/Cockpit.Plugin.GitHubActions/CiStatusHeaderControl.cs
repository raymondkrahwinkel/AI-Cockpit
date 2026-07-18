using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GitHubActions;

/// <summary>
/// The GitHub Actions status of the branch a session is working in, in that session's header (AC-52): a coloured icon
/// for the latest workflow run on the current branch — green pass, red fail, amber running — with the run's details on
/// hover, click to open it on GitHub. Mirrors the git-status header's per-session lifecycle: it re-reads when the
/// session's working directory becomes known and on a modest timer (a run's state changes on GitHub, not locally), and
/// stays out of the header entirely when there is no repo, no run, or no gh.
/// </summary>
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
        (_icon.Kind, var brush) = _Appearance(run.State);
        _icon.Foreground = brush;
        ToolTip.SetTip(_row, _Describe(run));
    }

    private static (MaterialIconKind Kind, IBrush Brush) _Appearance(CiRunState state) => state switch
    {
        CiRunState.Passed => (MaterialIconKind.CheckCircleOutline, _Brush("CockpitStatusDoneBrush", Color.Parse("#5AA576"))),
        CiRunState.Failed => (MaterialIconKind.CloseCircleOutline, _Brush("CockpitStatusErrorBrush", Color.Parse("#D9534F"))),
        CiRunState.Running => (MaterialIconKind.ProgressClock, _Brush("CockpitStatusWaitingBrush", Color.Parse("#E0A33E"))),
        _ => (MaterialIconKind.MinusCircleOutline, _Brush("CockpitTextFaintBrush", Color.Parse("#9AA0A6"))),
    };

    private static string _Describe(CiRun run)
    {
        var state = run.State switch
        {
            CiRunState.Passed => "passed",
            CiRunState.Failed => "failed",
            CiRunState.Running => "running",
            _ => string.IsNullOrEmpty(run.Conclusion) ? "unknown" : run.Conclusion,
        };
        var when = run.CreatedAt is { } at ? $" · {_Ago(at)}" : string.Empty;
        var workflow = string.IsNullOrEmpty(run.WorkflowName) ? "workflow" : run.WorkflowName;
        return $"CI: {workflow} on '{run.Branch}' — {state} ({run.Event}){when}\n\nClick to open the run on GitHub.";
    }

    private static string _Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.UtcNow - at;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours}h ago"
            : $"{(int)span.TotalDays}d ago";
    }

    private void _OpenRun()
    {
        if (_current is not { Url: { Length: > 0 } url } || !CiWorkflowRunClient.IsGitHubRunUrl(url))
        {
            return;
        }

        try
        {
            // UseShellExecute hands the URL to the OS's default handler (never a shell string), the standard way to
            // open a link cross-platform; the URL is validated to be an https github.com link first.
            using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening a browser is a convenience — a machine without a handler just does nothing.
        }
    }

    private static IBrush _Brush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : new SolidColorBrush(fallback);
}
