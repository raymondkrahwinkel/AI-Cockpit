using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The Autopilot workspace body: the plugin draws the whole surface (the host draws only the tab and the frame). It
/// tracks the run controller and re-renders on its change, showing the opstart flow (AC-151) — the empty state, the
/// scoping judgment, a refused point with its reason, or the running point with its isolated session embedded through
/// <see cref="IWorkspaceContext.EmbedSession"/> (AC-122/AC-85). The done-gate and the tracker channel land in later
/// sub-tickets; <paramref name="settings"/> feeds the profile and, later, gate config.
/// </summary>
internal sealed class AutopilotWorkspaceBody : UserControl
{
    private readonly IWorkspaceContext _context;
    private readonly AutopilotSettings _settings;
    private readonly AutopilotRunController _runs;
    private readonly ContentControl _bodyHost = new();
    private IEmbeddedSession? _embedded;
    private AutopilotRun? _embeddedRun;
    private Control? _runningView;

    public AutopilotWorkspaceBody(IWorkspaceContext context, AutopilotSettings settings, AutopilotRunController runs)
    {
        _context = context;
        _settings = settings;
        _runs = runs;

        var header = new Border
        {
            Padding = new Thickness(16, 12),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            [DockPanel.DockProperty] = Dock.Top,
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock { Text = "Autopilot", FontWeight = FontWeight.SemiBold, FontSize = 15 },
                    new TextBlock
                    {
                        Text = "Start a run from an issue's context menu — the pipeline, its live session and the done-gate will appear here.",
                        Opacity = 0.7,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = _Brush("CockpitTextSecondaryBrush"),
                    },
                },
            },
        };

        Content = new DockPanel { LastChildFill = true, Children = { header, _bodyHost } };
        _Render();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _runs.Changed += _OnChanged;
        _Render();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _runs.Changed -= _OnChanged;
        base.OnDetachedFromVisualTree(e);
    }

    // The controller advances from a background continuation (the scoping delegation awaits off the UI thread), so
    // marshal the render — and the EmbedSession it may do — back onto the UI thread.
    private void _OnChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _Render();
        }
        else
        {
            Dispatcher.UIThread.Post(_Render);
        }
    }

    private void _Render()
    {
        // A different point (or none) took over this surface: end the previous run's embedded session and its worktree
        // before anything new lands, so a replaced run is not left running orphaned.
        if (_embedded is { } previous && !ReferenceEquals(_embeddedRun, _runs.Current))
        {
            _embedded = null;
            _embeddedRun = null;
            _runningView = null;
            _ = previous.CloseAsync();
        }

        if (_runs.Current is not { } run)
        {
            _bodyHost.Content = _BuildEmptyState();
            return;
        }

        _bodyHost.Content = _runs.Phase switch
        {
            AutopilotRunPhase.Refused => _BuildRefusedView(run, _runs.RefusalReason),
            AutopilotRunPhase.Running => _BuildRunningView(run),
            _ => _BuildScopingView(run),
        };
    }

    private Control _BuildScopingView(AutopilotRun run) =>
        _BuildCentredCard(run, MaterialIconKind.MagnifyScan, "Scoping…", "Checking the point is workable before starting.", _Brush("CockpitTextSecondaryBrush"));

    private Control _BuildRefusedView(AutopilotRun run, string? reason) =>
        _BuildCentredCard(run, MaterialIconKind.CancelOutline, "Parked — not started", string.IsNullOrWhiteSpace(reason) ? "Scoping refused this point." : reason, _Brush("CockpitTextSecondaryBrush"));

    // The running point: a thin info strip naming it, then the embedded session filling the rest. Built once per run
    // and cached — re-rendering (a tab revisit re-attaches this same body) must not reparent the embedded view, which
    // Avalonia forbids. The session is embedded once (AC-122) on an isolated worktree (AC-85) from the active copy.
    private Control _BuildRunningView(AutopilotRun run)
    {
        if (_runningView is not null)
        {
            return _runningView;
        }

        _embedded = _context.EmbedSession(new EmbeddedSessionRequest
        {
            ProfileId = _settings.DefaultProfileLabel(),
            WorkingDirectory = _context.Sessions.ActiveSessionWorkingDirectory,
            IsolateInWorktree = true,
        });
        _embeddedRun = run;

        var strip = new Border
        {
            Padding = new Thickness(12, 8),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            [DockPanel.DockProperty] = Dock.Top,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    _TrackerChip(run.Tracker),
                    new TextBlock { Text = string.IsNullOrWhiteSpace(run.IssueId) ? "(unnamed issue)" : run.IssueId, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = run.Title, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = _Brush("CockpitTextSecondaryBrush") },
                },
            },
        };

        _runningView = new DockPanel { LastChildFill = true, Children = { strip, _embedded.View } };
        return _runningView;
    }

    private Control _BuildCentredCard(AutopilotRun run, MaterialIconKind icon, string title, string subtitle, IBrush? subtitleBrush) =>
        new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8,
            MaxWidth = 460,
            Children =
            {
                _TrackerChip(run.Tracker),
                new MaterialIcon { Kind = icon, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center, Foreground = _Brush("CockpitTextFaintBrush") },
                new TextBlock { Text = string.IsNullOrWhiteSpace(run.IssueId) ? "(unnamed issue)" : run.IssueId, HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeight.SemiBold, FontSize = 15 },
                new TextBlock { Text = title, HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = subtitle,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = subtitleBrush,
                },
            },
        };

    private Border _TrackerChip(string tracker) =>
        new()
        {
            Background = _Brush("CockpitSecondaryBgBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock { Text = tracker, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = _Brush("CockpitAccentBrush") },
        };

    private Control _BuildEmptyState() =>
        new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8,
            MaxWidth = 380,
            Children =
            {
                new MaterialIcon
                {
                    Kind = MaterialIconKind.RobotOutline,
                    Width = 32,
                    Height = 32,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = _Brush("CockpitTextFaintBrush"),
                },
                new TextBlock { Text = "No run yet", HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "Pick “Start in Autopilot” on an issue, and its run lands on this surface.",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = _Brush("CockpitTextFaintBrush"),
                },
            },
        };

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
