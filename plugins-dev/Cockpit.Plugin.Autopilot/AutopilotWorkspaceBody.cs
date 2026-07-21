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
using Cockpit.Plugins.Abstractions.Notifications;
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
    private Border? _statusStrip;
    private AutopilotRun? _reactRun;
    private AutopilotRunPhase? _stagedPhase;
    private bool _postedEvidence;
    private bool _approved;

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
        // A different point (or none) took over: reset the tracker-reaction state for the new run.
        if (!ReferenceEquals(_reactRun, _runs.Current))
        {
            _reactRun = _runs.Current;
            _stagedPhase = null;
            _postedEvidence = false;
            _approved = false;
        }

        // End the previous run's embedded session and its worktree before anything new lands, so a replaced run is not
        // left running orphaned.
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
            AutopilotRunPhase.Refused => _BuildRefusedView(run, _runs.BlockReason),
            AutopilotRunPhase.Running or AutopilotRunPhase.Blocked or AutopilotRunPhase.MergeReady => _BuildRunningView(run),
            _ => _BuildScopingView(run),
        };

        _ReactToTracker(run);
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
            // Re-render of the same run (a gate reported, a tab revisit): refresh the status strip in place — never
            // rebuild the view, which would reparent the embedded session.
            _UpdateStatusStrip(run);
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
        _runs.BindSession(embedded.PaneId);
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

            // Approved: hand the agent its work brief and mark the session as running the point. Only now may the run
            // touch the tracker — before the operator approved, no stage move or comment goes out.
            _ = _host.SendToSessionAsync(embedded.PaneId, _BuildWorkPrompt(run));
            _ = _host.SetSessionStatusline(embedded.PaneId, $"Autopilot · {run.IssueId}");
            _approved = true;
            _ReactToTracker(run);
        }
        catch (Exception)
        {
            // A failed confirmation must not crash the UI; the embedded session surfaces its own state.
        }
    }

    private Control _ComposeRunningView(AutopilotRun run, IEmbeddedSession embedded)
    {
        _statusStrip = new Border
        {
            Padding = new Thickness(12, 8),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            [DockPanel.DockProperty] = Dock.Top,
            Child = _StatusStripContent(run),
        };

        return new DockPanel { LastChildFill = true, Children = { _statusStrip, embedded.View } };
    }

    private void _UpdateStatusStrip(AutopilotRun run)
    {
        if (_statusStrip is not null)
        {
            _statusStrip.Child = _StatusStripContent(run);
        }
    }

    // The strip above the embedded session: names the point and reflects the run's state — its done-gates while it
    // runs (AC-153), merge-ready when they pass (the merge stays with you), or the block reason when a hard gate did not.
    private Control _StatusStripContent(AutopilotRun run)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_TrackerChip(run.Tracker));
        row.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(run.IssueId) ? "(unnamed issue)" : run.IssueId, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        switch (_runs.Phase)
        {
            case AutopilotRunPhase.MergeReady:
                row.Children.Add(_StateBadge("Merge-ready — review & merge the PR (the merge stays with you)", "CockpitAccentBrush"));
                break;
            case AutopilotRunPhase.Blocked:
                row.Children.Add(_StateBadge(_runs.BlockReason ?? "Blocked at a hard gate.", "CockpitTextSecondaryBrush"));
                break;
            default:
                foreach (var chip in _GateChips())
                {
                    row.Children.Add(chip);
                }

                break;
        }

        return row;
    }

    // One chip per gate: its short name, whether it is hard or skip, the outcome the agent has reported so far, and its
    // evidence (a review summary, a verify screenshot path) as the chip's tooltip.
    private IEnumerable<Control> _GateChips()
    {
        var gates = _runs.Gates;
        return new[] { (GateKind.Verify, "verify"), (GateKind.CodeReview, "code"), (GateKind.Security, "security"), (GateKind.Conventions, "conventions") }
            .Select(pair =>
            {
                var reported = gates.TryGetValue(pair.Item1, out var outcome);
                var hard = _settings.Gate(pair.Item1) == GateMode.Hard;
                var mark = !reported ? "·" : outcome switch
                {
                    AutopilotGateOutcome.Passed => "✓",
                    AutopilotGateOutcome.Failed => "✗",
                    _ => "–",
                };
                var chip = _StateBadge($"{pair.Item2} {mark}{(hard ? " (hard)" : string.Empty)}", reported && outcome == AutopilotGateOutcome.Passed ? "CockpitAccentBrush" : "CockpitTextFaintBrush");
                if (_runs.GateEvidence(pair.Item1) is { Length: > 0 } evidence)
                {
                    ToolTip.SetTip(chip, evidence);
                }

                return (Control)chip;
            });
    }

    private Border _StateBadge(string text, string foregroundKey) =>
        new()
        {
            Background = _Brush("CockpitSecondaryBgBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = _Brush(foregroundKey), TextTrimming = TextTrimming.CharacterEllipsis },
        };

    // Post evidence and move the tracker stage as the run advances (AC-154), through whichever tracker provider matches
    // the run — tracker-neutral. Fire-and-forget: a slow or failed tracker post must not stall the render, and each
    // provider action returns whether it landed rather than throwing.
    private void _ReactToTracker(AutopilotRun run)
    {
        // The run may only touch the tracker once the operator has approved it — never before or on a declined run.
        if (!_approved || _host.TrackerProviders.FirstOrDefault(provider => provider.TrackerId == run.Tracker) is not { } tracker)
        {
            return;
        }

        // Move the mapped stage once per phase transition; an unmapped phase moves only the session status.
        if (_stagedPhase != _runs.Phase)
        {
            _stagedPhase = _runs.Phase;
            if (_settings.StageFor(_runs.Phase) is { Length: > 0 } stage)
            {
                _ = _TrackAsync(tracker.SetStageAsync(run.IssueId, stage), "stage update");
            }
        }

        // Post the evidence comment once, when the run settles to merge-ready or blocked.
        if (!_postedEvidence && _runs.Phase is AutopilotRunPhase.MergeReady or AutopilotRunPhase.Blocked)
        {
            _postedEvidence = true;
            _ = _TrackAsync(tracker.PostCommentAsync(run.IssueId, _EvidenceComment()), "evidence comment");
        }
    }

    // Await a fire-and-forget tracker action and surface it when it did not land — a silent evidence pipeline is
    // worse than a noisy one for a feature whose point is an auditable trail.
    private async Task _TrackAsync(Task<bool> action, string what)
    {
        try
        {
            if (!await action)
            {
                _host.ShowToast($"Autopilot: the tracker {what} did not land.", PluginToastSeverity.Warning);
            }
        }
        catch (Exception)
        {
            _host.ShowToast($"Autopilot: the tracker {what} did not land.", PluginToastSeverity.Warning);
        }
    }

    private string _EvidenceComment()
    {
        var gates = _runs.Gates.Count == 0
            ? "none reported"
            : string.Join(", ", _runs.Gates.Select(gate => $"{gate.Key} {gate.Value}"));
        return _runs.Phase == AutopilotRunPhase.MergeReady
            ? $"Autopilot: merge-ready.{(_runs.PrUrl is { Length: > 0 } url ? $" PR: {url}." : string.Empty)} Gates — {gates}. The merge stays with a human."
            : $"Autopilot: blocked — {_runs.BlockReason}. Gates — {gates}.";
    }

    // The point's work brief the agent is handed once the operator approves (AC-152/AC-153): work it to a merge-ready
    // PR, run the done-gates and report them, then stop — the merge stays with the human (AC-94 principle #6).
    private string _BuildWorkPrompt(AutopilotRun run)
    {
        var description = run.Data.GetValueOrDefault("description", string.Empty);
        return $"""
            You are an Autopilot run. Work this issue to a merge-ready pull request, then stop — do NOT merge; a human
            does the merge.

            Steps:
            1. Work in this worktree: make the change, commit and push your branch, and open the PR.
            2. Run the done-gates and report each with the mcp__cockpit-autopilot__autopilot_gate tool
               (gate = verify | code | security | conventions; result = passed | failed | skipped):
               - verify: run the visual check with mcp__cockpit-verify__verify and judge it against the intent;
               - code: run /code-review over your diff;
               - security: run /security-review;
               - conventions: check the change follows the project's language and memory conventions.
               If a hard gate fails, fix what is in scope and re-run it — up to {_settings.MaxSelfFixAttempts()} times —
               before giving up.
            3. When the PR is open and every gate is reported, call mcp__cockpit-autopilot__autopilot_ready. Autopilot
               then settles the run to merge-ready or blocked.

            Issue ({run.Tracker} {run.IssueId}): {run.Title}
            {description}
            """;
    }

    // The start-consent surface (AC-152), shown over the embedded session: what the operator approves before the run
    // self-drives. The Action is rendered verbatim, so it is flattened to one line with control and format characters
    // (bidi overrides, zero-width) stripped — the tracker-supplied title must not reorder or hide what will run.
    private static ConsentRequest _StartConsent(AutopilotRun run, string mode, string paneId) => new(
        Title: "Autopilot wants to start an autonomous run",
        Action: _SingleLine($"Run {run.Tracker} {run.IssueId} autonomously ({mode}) to a merge-ready PR, posting progress and evidence back to {run.Tracker} and moving the issue's stage — the agent works without asking before edits; shell and egress stay gated. Issue: {run.Title}"),
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
