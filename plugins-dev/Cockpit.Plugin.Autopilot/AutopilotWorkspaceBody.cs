using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The Autopilot workspace body: the plugin draws the whole surface (the host draws only the tab and the frame). It
/// tracks the run controller and re-renders on its change, showing the opstart flow (AC-151/AC-152) — the empty state,
/// the scoping judgment, a refused point with its reason, or the running point with its isolated session embedded
/// through <see cref="IWorkspaceContext.EmbedSession"/> (AC-122/AC-85). The run is confirmed with the operator over
/// that embedded session before it is briefed and self-drives; the done-gate and tracker channel land in later
/// sub-tickets.
/// </summary>
internal sealed class AutopilotWorkspaceBody : UserControl
{
    private readonly ICockpitHost _host;
    private readonly IWorkspaceContext _context;
    private readonly AutopilotSettings _settings;
    private readonly AutopilotRunController _runs;
    private readonly ContentControl _bodyHost = new();
    private IEmbeddedSession? _embedded;
    private AutopilotRun? _embeddedRun;
    private Control? _runningView;

    public AutopilotWorkspaceBody(ICockpitHost host, IWorkspaceContext context, AutopilotSettings settings, AutopilotRunController runs)
    {
        _host = host;
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

    // The running point: embed the isolated session once (AC-122/AC-85), show it, and confirm + brief it out of band.
    // Built once per run and cached — re-rendering (a tab revisit re-attaches this same body) must not reparent the
    // embedded view, which Avalonia forbids.
    private Control _BuildRunningView(AutopilotRun run)
    {
        if (_runningView is not null)
        {
            return _runningView;
        }

        var embedded = _context.EmbedSession(new EmbeddedSessionRequest
        {
            ProfileId = _settings.DefaultProfileLabel(),
            WorkingDirectory = _context.Sessions.ActiveSessionWorkingDirectory,
            IsolateInWorktree = true,
            PermissionMode = _settings.AutonomyMode(),
        });
        _embedded = embedded;
        _embeddedRun = run;
        _ = _host.SetSessionStatusline(embedded.PaneId, $"Autopilot · {run.IssueId} — awaiting approval");
        _runningView = _ComposeRunningView(run, embedded);

        // Confirm over the now-visible embedded session, then brief it — posted so it runs after this render returns
        // rather than re-entering it (which a synchronous consent decision would otherwise do mid-render).
        Dispatcher.UIThread.Post(() => _ = _ConfirmAndBriefAsync(run, embedded));
        return _runningView;
    }

    private async Task _ConfirmAndBriefAsync(AutopilotRun run, IEmbeddedSession embedded)
    {
        try
        {
            var decision = await _host.RequestConsentAsync(_StartConsent(run, _settings.AutonomyMode(), embedded.PaneId));

            // Superseded while the operator decided: a newer run owns the surface now and the old session is already
            // being closed by the run-change reset.
            if (!ReferenceEquals(_embeddedRun, run))
            {
                return;
            }

            if (!decision.IsApproved)
            {
                _embedded = null;
                _embeddedRun = null;
                _runningView = null;
                await embedded.CloseAsync();
                _runs.Refuse("Autonomous run declined by the operator.");
                return;
            }

            // Approved: hand the agent its work brief and mark the session as running the point.
            _ = _host.SendToSessionAsync(embedded.PaneId, _BuildWorkPrompt(run));
            _ = _host.SetSessionStatusline(embedded.PaneId, $"Autopilot · {run.IssueId}");
        }
        catch (Exception)
        {
            // A failed confirmation must not crash the UI; the embedded session surfaces its own state.
        }
    }

    private Control _ComposeRunningView(AutopilotRun run, IEmbeddedSession embedded)
    {
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

        return new DockPanel { LastChildFill = true, Children = { strip, embedded.View } };
    }

    // The point's work brief the agent is handed once the operator approves (AC-152): work it to a merge-ready PR, and
    // stop there — the merge stays with the human (AC-94 principle #6).
    private static string _BuildWorkPrompt(AutopilotRun run)
    {
        var description = run.Data.GetValueOrDefault("description", string.Empty);
        return $"""
            You are an Autopilot run. Work this issue to a merge-ready pull request, then stop — do NOT merge; a human
            does the merge. Work in this worktree: make the change, commit and push your branch, and open the PR.

            Issue ({run.Tracker} {run.IssueId}): {run.Title}
            {description}
            """;
    }

    // The start-consent surface (AC-152), shown over the embedded session: what the operator approves before the run
    // self-drives. The Action is rendered verbatim, so it is flattened to one line with control and format characters
    // (bidi overrides, zero-width) stripped — the tracker-supplied title must not reorder or hide what will run.
    private static ConsentRequest _StartConsent(AutopilotRun run, string mode, string paneId) => new(
        Title: "Autopilot wants to start an autonomous run",
        Action: _SingleLine($"Run {run.Tracker} {run.IssueId} autonomously ({mode}) to a merge-ready PR — the agent works without asking before edits; shell and egress stay gated. Issue: {run.Title}"),
        Source: new ConsentSource(paneId, "autopilot", "Autopilot"),
        Scope: "autopilot.start",
        Risk: ConsentRisk.Dangerous);

    private static string _SingleLine(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsControl(ch) || char.GetUnicodeCategory(ch) == UnicodeCategory.Format)
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }

                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString().Trim();
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
