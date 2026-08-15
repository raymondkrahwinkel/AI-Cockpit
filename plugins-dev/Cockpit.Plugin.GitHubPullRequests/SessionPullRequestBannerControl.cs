using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GitHubPullRequests;

// AC-802: the PR/CI banner under a session's transcript, collapsed and expanded. Mirrors
// Cockpit.Plugin.GitHubActions.CiStatusHeaderControl's per-session lifecycle and _Appearance mapping exactly.
internal sealed class SessionPullRequestBannerControl : UserControl
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly IPluginSessionContext _session;
    private readonly SessionPullRequestStatusClient _client = new();
    private readonly DispatcherTimer _refresh;

    private readonly MaterialIcon _statusIcon;
    private readonly TextBlock _prNumberText;
    private readonly TextBlock _repoText;
    private readonly TextBlock _branchText;
    private readonly TextBlock _additionsText;
    private readonly TextBlock _deletionsText;
    private readonly TextBlock _ciSummaryText;
    private readonly MaterialIcon _chevron;
    private readonly Button _collapsedRow;
    private readonly StackPanel _checksList;
    private readonly Border _expandedPanel;

    private SessionPullRequestStatus? _current;
    private bool _expanded;
    private int _loadToken;
    private CancellationTokenSource? _loadCts;

    public SessionPullRequestBannerControl(IPluginSessionContext session)
    {
        _session = session;

        _statusIcon = new MaterialIcon { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        _prNumberText = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _repoText = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Foreground = _Brush("CockpitTextSecondaryBrush", "#949aa5") };
        _branchText = new TextBlock { FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
        _additionsText = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Foreground = _Brush("CockpitStatusDoneBrush", "#5AA576") };
        _deletionsText = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Foreground = _Brush("CockpitStatusErrorBrush", "#D64545") };
        _ciSummaryText = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        _chevron = new MaterialIcon { Kind = MaterialIconKind.ChevronDown, Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center };

        var branchTag = new Border { Padding = new Thickness(6, 1), Child = _branchText, VerticalAlignment = VerticalAlignment.Center };
        branchTag.Classes.Add("tag");

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _statusIcon, _prNumberText, _repoText, branchTag, _additionsText, _deletionsText },
        };
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _ciSummaryText, _chevron },
        };
        var row = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        row.Children.Add(left);

        _collapsedRow = new Button
        {
            Content = row,
            Padding = new Thickness(8, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        _collapsedRow.Click += (_, _) => _ToggleExpanded();

        _checksList = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
        _expandedPanel = new Border
        {
            Padding = new Thickness(8, 0, 8, 8),
            IsVisible = false,
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = "CI MONITORING", FontSize = 10, FontWeight = FontWeight.SemiBold,
                        Foreground = _Brush("CockpitTextFaintBrush", "#656c78"),
                    },
                    _checksList,
                },
            },
        };

        var root = new StackPanel { Children = { _collapsedRow, _expandedPanel } };

        Content = new Border
        {
            Background = _Brush("CockpitSecondaryBgBrush", "#0c0e12"),
            BorderBrush = _Brush("CockpitHairlineBrush", "#2a2f39"),
            BorderThickness = new Thickness(1),
            CornerRadius = _Radius("CockpitControlRadius", new CornerRadius(6)),
            Child = root,
        };
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
        SessionPullRequestStatus? status;
        try
        {
            status = await _client.GetOpenPullRequestAsync(directory, cts.Token);
        }
        catch (Exception)
        {
            status = null;
        }

        if (token != _loadToken)
        {
            return; // a newer load superseded this one
        }

        _current = status;
        if (status is null)
        {
            IsVisible = false;
            return;
        }

        IsVisible = true;
        _Render(status);
    }

    private void _Render(SessionPullRequestStatus status)
    {
        (_statusIcon.Kind, var brush) = _Appearance(status.OverallState);
        _statusIcon.Foreground = brush;
        _prNumberText.Text = $"PR #{status.Number}";
        _repoText.Text = status.Repository;
        _branchText.Text = status.Branch;
        _additionsText.Text = $"+{status.Additions}";
        _additionsText.IsVisible = status.Additions > 0;
        _deletionsText.Text = $"-{status.Deletions}";
        _deletionsText.IsVisible = status.Deletions > 0;
        _ciSummaryText.Text = _CiSummaryText(status);

        _checksList.Children.Clear();
        foreach (var check in status.Checks)
        {
            _checksList.Children.Add(_CheckRow(check));
        }
    }

    private void _ToggleExpanded()
    {
        _expanded = !_expanded;
        _expandedPanel.IsVisible = _expanded;
        _chevron.Kind = _expanded ? MaterialIconKind.ChevronUp : MaterialIconKind.ChevronDown;
    }

    private static Control _CheckRow(PullRequestCheck check)
    {
        (var kind, var brush) = _Appearance(check.State);
        var icon = new MaterialIcon { Kind = kind, Width = 13, Height = 13, Foreground = brush, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = check.Name, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        var status = new TextBlock
        {
            Text = _StatusLabel(check.State), FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Foreground = _Brush("CockpitTextSecondaryBrush", "#949aa5"), Margin = new Thickness(8, 0, 0, 0),
        };
        var duration = new TextBlock
        {
            Text = _FormatDuration(check.Duration), FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Foreground = _Brush("CockpitTextFaintBrush", "#656c78"),
        };

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { icon, name, status } };
        var row = new DockPanel();
        DockPanel.SetDock(duration, Dock.Right);
        row.Children.Add(duration);
        row.Children.Add(left);
        return row;
    }

    private static string _StatusLabel(PullRequestCheckState state) => state switch
    {
        PullRequestCheckState.Passed => "Passed",
        PullRequestCheckState.Failed => "Failed",
        PullRequestCheckState.Running => "In progress",
        _ => "Skipped",
    };

    private static string _FormatDuration(TimeSpan? duration)
    {
        if (duration is not { } value || value < TimeSpan.Zero)
        {
            return string.Empty;
        }

        return value.TotalMinutes >= 1 ? $"{(int)value.TotalMinutes}m {value.Seconds:D2}s" : $"{(int)value.TotalSeconds}s";
    }

    private static string _CiSummaryText(SessionPullRequestStatus status) =>
        status.ChecksFailed > 0 ? $"CI {status.ChecksFailed} failed"
        : status.ChecksTotal > 0 ? $"CI {status.ChecksPassed}/{status.ChecksTotal}"
        : "CI —";

    // Identical to CiStatusHeaderControl._Appearance (AC-802's own instruction: "kleuren/iconen exact overnemen"),
    // duplicated rather than shared because the two controls live in different plugin assemblies and the source
    // is internal to Cockpit.Plugin.GitHubActions, which this plugin does not — and must not — reference.
    private static (MaterialIconKind Kind, IBrush Brush) _Appearance(PullRequestCheckState state) => state switch
    {
        PullRequestCheckState.Passed => (MaterialIconKind.CheckCircleOutline, _Brush("CockpitStatusDoneBrush", "#5AA576")),
        PullRequestCheckState.Failed => (MaterialIconKind.CloseCircleOutline, _Brush("CockpitStatusErrorBrush", "#D64545")),
        PullRequestCheckState.Running => (MaterialIconKind.ProgressClock, _Brush("CockpitStatusWaitingBrush", "#E0A33E")),
        _ => (MaterialIconKind.MinusCircleOutline, _Brush("CockpitTextFaintBrush", "#656c78")),
    };

    // The host's theme brush, resolved at call time so a repaint of the token is followed rather than frozen. The
    // fallback hex is only reached with no `Application` (designer/headless) and is held equal to the token it
    // stands in for by the repository's theme guard.
    private static IBrush _Brush(string key, string fallbackHex) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallbackHex));

    private static CornerRadius _Radius(string key, CornerRadius fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is CornerRadius radius
            ? radius
            : fallback;
}
